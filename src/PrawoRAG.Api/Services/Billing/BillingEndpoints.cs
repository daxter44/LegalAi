using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrawoRAG.Api.Services.Auth;
using PrawoRAG.Storage;
using Stripe;
using Stripe.Checkout;

namespace PrawoRAG.Api.Services.Billing;

/// <summary>
/// Ścieżka płatnicza (E3/US-3.1 — spike). Świadome decyzje, każda z powodem:
///
/// 1. <b>Zwykłe endpointy HTTP, nie komponenty interaktywne.</b> Powrót ze Stripe to nowe załadowanie
///    strony i NOWY obwód SignalR — logika w komponencie trafiałaby na rozjazd stanu.
/// 2. <b>Webhook to jedyne źródło prawdy.</b> Powrót użytkownika na adres sukcesu nie nadaje planu:
///    ten adres da się otworzyć ręcznie, bez płacenia.
/// 3. <b>Podpis webhooka weryfikowany zawsze</b>, a sam endpoint jest wyłączony z antiforgery (Stripe
///    nie ma skąd wziąć naszego tokenu) — bez weryfikacji podpisu każdy nadałby sobie plan POST-em.
/// 4. <b>Idempotencja przez klucz w bazie.</b> Zdarzenia przychodzą co najmniej raz; duplikat rozbija
///    się o klucz główny <c>processed_webhooks</c> i kończy odpowiedzią 200 (żeby Stripe przestał ponawiać).
/// 5. <b>Zawsze 200 na poprawnie podpisane zdarzenie</b>, którego nie obsługujemy — inaczej Stripe
///    ponawia w nieskończoność zdarzenia, które nas nie interesują.
/// </summary>
public static class BillingEndpoints
{
    private const string TokenField = "__RequestVerificationToken";

    public static void MapBillingEndpoints(this WebApplication app)
    {
        // --- strona konta: pokazuje plan + guziki do Checkout/portalu (spike, bez UI Blazor — patrz p.1 wyżej) ---
        app.MapGet("/konto", async (HttpContext http, IAntiforgery af,
            UserManager<Storage.Entities.AppUserEntity> users,
            PrawoRAG.Api.Services.CostGuard guard,
            PrawoRAG.Api.Services.Plans.IEntitlements entitlements,
            Microsoft.Extensions.Options.IOptions<PrawoRAG.Api.Services.AnalysisOptions> analysis) =>
        {
            if (http.User.FindFirstValue(ClaimTypes.NameIdentifier) is not { } userId)
                return Results.Unauthorized();
            var user = await users.FindByIdAsync(userId);
            if (user is null) return Results.Unauthorized();

            // Zużycie X/Y w bieżącym okresie (leftover RED, addytywne — dane już liczone przez
            // CostGuard). Best-effort: awaria licznika nie może odbierać dostępu do strony konta.
            (int Used, int Limit)? usage = null;
            DateTime? periodEndUtc = null;
            try
            {
                usage = await guard.UsageAsync(userId);
                if (usage is not null)
                    periodEndUtc = (await entitlements.ForAsync(userId)).Period.EndUtc;
            }
            catch { /* best-effort */ }

            var token = af.GetAndStoreTokens(http).RequestToken ?? "";
            return Results.Content(
                BillingPages.Konto(TokenField, token, user.PlanId, user.PlanStatus,
                    user.PlanValidUntilUtc, hasSubscription: !string.IsNullOrEmpty(user.StripeCustomerId),
                    email: user.Email, emailConfirmed: user.EmailConfirmed,
                    analysisEnabled: analysis.Value.Enabled,
                    usage: usage, periodEndUtc: periodEndUtc),
                "text/html; charset=utf-8");
        }).RequireAuthorization();

        // --- start zakupu: Checkout (przekierowanie na stronę Stripe) ---
        app.MapPost("/platnosc/start", async (
            HttpContext http, IAntiforgery af, IOptions<BillingOptions> billing, IOptions<AuthOptions> auth,
            UserManager<Storage.Entities.AppUserEntity> users, ILoggerFactory logs) =>
        {
            if (!await Valid(http, af)) return Results.BadRequest("Formularz wygasł — odśwież stronę.");
            if (http.User.FindFirstValue(ClaimTypes.NameIdentifier) is not { } userId)
                return Results.Unauthorized();

            var o = billing.Value;
            var user = await users.FindByIdAsync(userId);
            if (user is null) return Results.Unauthorized();

            var baseUrl = AuthLinks.Absolute(auth.Value.PublicBaseUrl, http.Request.Scheme,
                http.Request.Host.Value ?? "", "");

            var options = new SessionCreateOptions
            {
                Mode = "subscription",
                LineItems = [new SessionLineItemOptions { Price = o.PriceId, Quantity = 1 }],
                SuccessUrl = $"{baseUrl}/platnosc/powrot?stan=ok",
                CancelUrl = $"{baseUrl}/platnosc/powrot?stan=anulowano",
                // Wiążemy sesję z KONTEM, nie z adresem e-mail: e-mail można zmienić, identyfikator nie.
                ClientReferenceId = user.Id,
                Customer = string.IsNullOrEmpty(user.StripeCustomerId) ? null : user.StripeCustomerId,
                CustomerEmail = string.IsNullOrEmpty(user.StripeCustomerId) ? user.Email : null,
                SubscriptionData = new SessionSubscriptionDataOptions
                {
                    Metadata = new Dictionary<string, string> { ["app_user_id"] = user.Id },
                },
            };

            var session = await new SessionService().CreateAsync(options);
            logs.CreateLogger("PrawoRAG.Billing")
                .LogInformation("Checkout otwarty dla konta {UserId}.", user.Id);
            return Results.Redirect(session.Url);
        }).RequireAuthorization();

        // --- powrót ze Stripe: TYLKO informacja, żadnego nadawania uprawnień ---
        app.MapGet("/platnosc/powrot", (string? stan) => Results.Content(
            AuthPages.Message(
                stan == "ok" ? "Dziękujemy za zakup" : "Płatność przerwana",
                stan == "ok"
                    ? "Potwierdzenie od operatora płatności może dotrzeć w ciągu kilkunastu sekund — " +
                      "plan pojawi się na koncie automatycznie."
                    : "Nic nie zostało pobrane. Możesz wrócić do zakupu w dowolnym momencie.",
                ok: stan == "ok"),
            "text/html; charset=utf-8"));

        // --- portal klienta: zmiana karty, planu, anulowanie, historia płatności ---
        app.MapPost("/platnosc/portal", async (
            HttpContext http, IAntiforgery af, IOptions<AuthOptions> auth,
            UserManager<Storage.Entities.AppUserEntity> users) =>
        {
            if (!await Valid(http, af)) return Results.BadRequest("Formularz wygasł — odśwież stronę.");
            if (http.User.FindFirstValue(ClaimTypes.NameIdentifier) is not { } userId)
                return Results.Unauthorized();

            var user = await users.FindByIdAsync(userId);
            if (user?.StripeCustomerId is not { Length: > 0 } customerId)
                return Results.BadRequest("To konto nie ma jeszcze subskrypcji.");

            var baseUrl = AuthLinks.Absolute(auth.Value.PublicBaseUrl, http.Request.Scheme,
                http.Request.Host.Value ?? "", "");
            var session = await new Stripe.BillingPortal.SessionService().CreateAsync(
                new Stripe.BillingPortal.SessionCreateOptions
                {
                    Customer = customerId,
                    ReturnUrl = $"{baseUrl}/czat",
                });

            return Results.Redirect(session.Url);
        }).RequireAuthorization();

        // --- webhook: jedyne miejsce nadające i odbierające plan ---
        app.MapPost("/platnosc/webhook", async (
            HttpContext http, IOptions<BillingOptions> billing, PrawoRagDbContext db,
            TimeProvider time, ILoggerFactory logs, CancellationToken ct) =>
        {
            var log = logs.CreateLogger("PrawoRAG.Billing");
            var o = billing.Value;
            var payload = await new StreamReader(http.Request.Body).ReadToEndAsync(ct);

            // .ToString() na StringValues, NIE niejawna konwersja: brak nagłówka daje wtedy null
            // (nie pusty string), a Stripe.net dereferencjuje go bez sprawdzenia — NullReferenceException
            // zamiast czystego 400 (złapane żywcem: curl bez nagłówka -> 500).
            var signature = http.Request.Headers["Stripe-Signature"].ToString();

            Event stripeEvent;
            try
            {
                stripeEvent = EventUtility.ConstructEvent(payload, signature, o.WebhookSecret);
            }
            catch (StripeException ex)
            {
                // Zły podpis = to nie jest zdarzenie od Stripe. 400 i ani słowa więcej.
                log.LogWarning("Odrzucony webhook: nieprawidłowy podpis ({Reason}).", ex.StripeError?.Message ?? "brak");
                return Results.BadRequest();
            }

            // Idempotencja: klucz główny nie pozwoli przetworzyć tego samego zdarzenia dwa razy.
            db.ProcessedWebhooks.Add(new Storage.Entities.ProcessedWebhookEntity
            {
                EventId = stripeEvent.Id,
                EventType = stripeEvent.Type,
                ProcessedAtUtc = time.GetUtcNow().UtcDateTime,
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Już przetworzone — 200, żeby Stripe przestał ponawiać.
                log.LogInformation("Webhook {EventId} już przetworzony — pomijam.", stripeEvent.Id);
                return Results.Ok();
            }

            var state = Map(stripeEvent);
            if (state is null) return Results.Ok(); // zdarzenie spoza naszego zakresu — świadomie 200

            var user = await FindUserAsync(db, stripeEvent, state.Value, ct);
            if (user is null)
            {
                log.LogWarning("Webhook {Type}: nie znaleziono konta dla klienta {Customer}.",
                    stripeEvent.Type, state.Value.CustomerId);
                return Results.Ok(); // nie ponawiamy — konta i tak nie przybędzie
            }

            if (SubscriptionSync.Apply(user, state.Value, o, time.GetUtcNow().UtcDateTime))
            {
                await db.SaveChangesAsync(ct);
                log.LogInformation("Webhook {Type}: konto {UserId} → plan {Plan}/{Status} do {Until}.",
                    stripeEvent.Type, user.Id, user.PlanId, user.PlanStatus, user.PlanValidUntilUtc);
            }
            else
            {
                log.LogInformation("Webhook {Type} starszy niż stan konta {UserId} — zignorowany.",
                    stripeEvent.Type, user.Id);
            }

            return Results.Ok();
        }).DisableAntiforgery(); // Stripe nie ma skąd wziąć tokenu; chroni podpis, nie antiforgery
    }

    /// <summary>
    /// Ze zdarzenia Stripe wyciąga tylko to, co potrzebne do decyzji. Obsługujemy wyłącznie zdarzenia
    /// SUBSKRYPCJI: to one niosą pełny stan. <c>checkout.session.completed</c> celowo pomijamy —
    /// zaraz po nim i tak przychodzi <c>customer.subscription.created</c> z kompletem danych.
    /// </summary>
    private static SubscriptionState? Map(Event e)
    {
        if (e.Data.Object is not Subscription sub) return null;

        return e.Type switch
        {
            "customer.subscription.created" or "customer.subscription.updated"
                or "customer.subscription.paused" or "customer.subscription.resumed" =>
                Build(sub, e, deleted: false),
            "customer.subscription.deleted" => Build(sub, e, deleted: true),
            _ => null,
        };
    }

    private static SubscriptionState Build(Subscription sub, Event e, bool deleted)
    {
        // Okres rozliczeniowy siedzi na pozycji subskrypcji (od API 2025-03; wcześniej był na samej
        // subskrypcji). Bierzemy z pierwszej pozycji — mamy jeden plan na subskrypcję.
        var item = sub.Items?.Data?.FirstOrDefault();
        // Anulowanie zaplanowane na koniec okresu potrafi przyjść na dwa sposoby: klasyczne
        // `cancel_at_period_end=true`, ALBO (zaobserwowane żywcem z Customer Portalu na API
        // 2026-08-26.dahlia) samo `cancel_at` ustawione na moment końca okresu, z `cancel_at_period_end`
        // wciąż `false`. Sprawdzamy oba, inaczej rezygnacja zgłoszona w portalu nigdy nie zmienia
        // statusu na "canceled" mimo poprawnie zaplanowanego końca dostępu po stronie Stripe.
        var cancelScheduled = sub.CancelAtPeriodEnd || sub.CancelAt is not null;
        return new SubscriptionState(
            CustomerId: sub.CustomerId ?? "",
            SubscriptionId: sub.Id,
            Status: sub.Status ?? "",
            CurrentPeriodStartUtc: item?.CurrentPeriodStart,
            CurrentPeriodEndUtc: item?.CurrentPeriodEnd,
            CancelAtPeriodEnd: cancelScheduled,
            EventTimeUtc: e.Created,
            Deleted: deleted);
    }

    /// <summary>
    /// Konto rozpoznajemy po metadanej z identyfikatorem konta (ustawianej przy Checkoucie), a gdy jej
    /// nie ma — po identyfikatorze klienta Stripe zapisanym na koncie.
    /// </summary>
    private static async Task<Storage.Entities.AppUserEntity?> FindUserAsync(
        PrawoRagDbContext db, Event e, SubscriptionState state, CancellationToken ct)
    {
        if (e.Data.Object is Subscription { Metadata: { } meta }
            && meta.TryGetValue("app_user_id", out var appUserId)
            && !string.IsNullOrEmpty(appUserId))
        {
            var byId = await db.Users.FirstOrDefaultAsync(u => u.Id == appUserId, ct);
            if (byId is not null) return byId;
        }

        return string.IsNullOrEmpty(state.CustomerId)
            ? null
            : await db.Users.FirstOrDefaultAsync(u => u.StripeCustomerId == state.CustomerId, ct);
    }

    private static async Task<bool> Valid(HttpContext http, IAntiforgery af)
    {
        try { await af.ValidateRequestAsync(http); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }
}
