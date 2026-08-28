using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrawoRAG.Storage;

namespace PrawoRAG.Api.Services.Plans;

/// <summary>
/// Co wolno konkretnemu użytkownikowi. <see cref="Limits"/> = <c>null</c> oznacza „plan nie obowiązuje"
/// (tryb bez kont: dev albo bramka na kody zaproszeń) — wtedy pilnują wyłącznie globalne capy pojemności.
/// </summary>
public sealed record Entitlement(string UserId, string PlanId, PlanLimits? Limits, BillingPeriod Period)
{
    public bool PlanApplies => Limits is not null;
}

/// <summary>Jedyne miejsce odpowiadające na pytanie „co wolno temu użytkownikowi".</summary>
public interface IEntitlements
{
    Task<Entitlement> ForAsync(string userId, CancellationToken ct = default);
}

/// <summary>
/// Uprawnienie czytane WYŁĄCZNIE z naszej bazy (E1/T-9). Nigdy nie pytamy dostawcy płatności
/// w ścieżce zapytania: jego awaria nie może odciąć dostępu komuś, kto zapłacił, a jego opóźnienie
/// nie może dokładać sekund do czasu odpowiedzi. E3 tylko zapisuje tu stan z webhooków.
///
/// Wygaśnięcie planu płatnego obsługujemy przy odczycie (data w przeszłości → plan darmowy), więc
/// nie potrzeba żadnego zadania w tle, które musiałoby zdążyć zadziałać.
/// </summary>
public sealed class Entitlements(
    IServiceScopeFactory scopes,
    IOptions<PlanOptions> plans,
    TimeProvider time) : IEntitlements
{
    public async Task<Entitlement> ForAsync(string userId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var o = plans.Value;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();

        var account = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.PlanId, u.PlanStatus, u.PlanValidUntilUtc, u.BillingAnchorUtc, u.CreatedAtUtc })
            .FirstOrDefaultAsync(ct);

        // Brak konta = tryb bez kont (dev, bramka invite). Zachowanie sprzed planów: limitów planu
        // nie ma, zostają globalne capy dzienne. Bez tego włączenie planów zmieniłoby dev i alfę.
        if (account is null)
            return new Entitlement(userId, PlanIds.Free, null,
                BillingPeriodCalculator.Current(now, now));

        var anchor = account.BillingAnchorUtc ?? account.CreatedAtUtc;
        if (anchor == default) anchor = now; // konto sprzed pola CreatedAtUtc — nie wywracamy się

        // Plan płatny wygasa PO TERMINIE — sprawdzenie przy odczycie, nie zadaniem w tle.
        //
        // UWAGA: sam status „anulowany" NIE odbiera dostępu. Rezygnacja w połowie okresu oznacza
        // „nie przedłużaj", a nie „zabierz to, co opłacone" — dostęp kończy się dopiero z datą
        // ważności. Konto, któremu subskrypcja faktycznie się skończyła, ma tę datę wyzerowaną
        // przez webhook (SubscriptionSync), więc spada na plan darmowy natychmiast.
        var expired = account.PlanValidUntilUtc is not { } until || until <= now;
        var planId = expired && account.PlanId != o.DefaultPlan
            ? o.DefaultPlan
            : account.PlanId;

        return new Entitlement(userId, planId, o.Resolve(planId),
            BillingPeriodCalculator.Current(anchor, now));
    }
}

/// <summary>Stany uprawnienia. <c>PastDue</c> to NIE to samo co brak dostępu — patrz E3/US-3.7.</summary>
public static class PlanStatuses
{
    public const string Active = "active";
    public const string PastDue = "past_due";
    public const string Canceled = "canceled";
}
