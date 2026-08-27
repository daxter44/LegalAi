using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using PrawoRAG.Storage.Entities;

namespace PrawoRAG.Api.Services.Auth;

/// <summary>
/// Rejestracja, logowanie, potwierdzenie adresu i reset hasła (E1, blok A).
///
/// Świadome decyzje bezpieczeństwa — każda ma tu swój powód, żeby nikt ich później nie „uprościł":
/// 1. <b>Antiforgery jawnie</b> — minimalne API czyta formularz przez <c>Request.Form</c>, więc
///    automatyczna walidacja middleware'u go nie obejmuje. Walidujemy sami w każdym POST.
/// 2. <b>Brak wyliczania kont</b> — rejestracja na zajęty adres i reset hasła na nieznany adres dają
///    DOKŁADNIE tę samą odpowiedź co przypadek pozytywny. Informację dostaje wyłącznie właściciel
///    skrzynki, listem.
/// 3. <b>Blokada po nieudanych próbach</b> (<c>lockoutOnFailure: true</c>) — hamuje zgadywanie haseł;
///    obok tego działa limiter HTTP „auth".
/// 4. <b>Wymagany potwierdzony adres</b> — inaczej darmowy limit byłby dostępny na dowolny zmyślony
///    adres, a reset hasła stałby się kanałem spamu.
/// 5. <b>Tylko lokalne przekierowania</b> po zalogowaniu — parametr powrotu z zewnętrznym adresem
///    to klasyczny open redirect wykorzystywany w phishingu.
/// 6. <b>Token w odnośniku nigdy nie trafia do logu</b> ani do komunikatu błędu.
/// </summary>
public static class AuthEndpoints
{
    // Nazwa pola MUSI być tą, której oczekuje walidacja antiforgery (AntiforgeryOptions.FormFieldName).
    private const string TokenField = "__RequestVerificationToken";
    private const int LinkValidHours = 6;

    // Serwerowy strop długości hasła (formularzowe maxlength to tylko sugestia dla przeglądarki):
    // PBKDF2 liczy się od pełnej długości wejścia, więc megabajtowe „hasło" to tani DoS.
    private const int MaxPasswordLength = 256;

    // Sztuczny hash do wyrównania czasu odpowiedzi, gdy konto nie istnieje (patrz logowanie).
    private static readonly PasswordHasher<AppUserEntity> DummyHasher = new();
    private static readonly string DummyPasswordHash =
        DummyHasher.HashPassword(null!, Guid.NewGuid().ToString("N"));

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("").RequireRateLimiting("auth");

        // --- rejestracja ---------------------------------------------------------------------

        group.MapGet("/rejestracja", (HttpContext http, IAntiforgery af) =>
            Html(AuthPages.Register(TokenField, Token(http, af), null, null)));

        group.MapPost("/rejestracja", async (
            HttpContext http, IAntiforgery af,
            UserManager<AppUserEntity> users, IAppEmailSender mail,
            IOptions<AuthOptions> auth, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (!await Valid(http, af)) return Antiforgery(http, af);

            var form = await http.Request.ReadFormAsync(ct);
            var email = form["email"].ToString().Trim();
            var displayName = Trim(form["displayName"].ToString(), 200);
            var password = form["password"].ToString();
            var terms = form["terms"].ToString();

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
                return Html(AuthPages.Register(TokenField, Token(http, af), email, displayName,
                    ["Podaj adres e-mail i hasło."]));
            if (password.Length > MaxPasswordLength)
                return Html(AuthPages.Register(TokenField, Token(http, af), email, displayName,
                    [$"Hasło może mieć najwyżej {MaxPasswordLength} znaków."]));
            if (terms is not "tak")
                return Html(AuthPages.Register(TokenField, Token(http, af), email, displayName,
                    ["Akceptacja regulaminu i polityki prywatności jest wymagana."]));

            var log = logs.CreateLogger("PrawoRAG.Auth");
            var existing = await users.FindByEmailAsync(email);
            if (existing is not null)
            {
                // Ta sama odpowiedź co przy sukcesie (patrz decyzja 2). Właściciel skrzynki dostaje
                // list z informacją, że ktoś próbował — reszta świata nie dowiaduje się niczego.
                await TrySend(mail, log, existing.Email!, EmailTemplates.AccountAlreadyExists(
                    AuthPages.ProductName, existing.DisplayName,
                    Absolute(http, auth.Value, "/logowanie"), Absolute(http, auth.Value, "/haslo/reset")), ct);
                return Html(MailboxPage());
            }

            var user = new AppUserEntity
            {
                UserName = email,
                Email = email,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
                CreatedAtUtc = DateTime.UtcNow,
                TermsAcceptedAtUtc = DateTime.UtcNow,
                TermsVersion = auth.Value.TermsVersion,
            };

            var created = await users.CreateAsync(user, password);
            if (!created.Succeeded)
            {
                // Wyścig z wcześniejszym FindByEmailAsync: gdy drugi wniosek o ten sam adres wpadnie
                // między sprawdzeniem a zapisem, CreateAsync zwraca Duplicate*. Pokazanie tego błędu
                // zdradzałoby istnienie konta — odpowiadamy identycznie jak przy zajętym adresie.
                if (created.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail)
                                                   or nameof(IdentityErrorDescriber.DuplicateUserName)))
                    return Html(MailboxPage());

                return Html(AuthPages.Register(TokenField, Token(http, af), email, displayName,
                    created.Errors.Select(e => e.Description)));
            }

            await SendConfirmationAsync(users, mail, log, user, http, auth.Value, ct);
            log.LogInformation("Nowe konto {UserId} — wysłano potwierdzenie adresu.", user.Id);
            return Html(MailboxPage());
        });

        // --- potwierdzenie adresu ------------------------------------------------------------

        group.MapGet("/potwierdz-email", async (
            HttpContext http, UserManager<AppUserEntity> users, string? id, string? kod) =>
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(kod))
                return Html(AuthPages.Message("Nieprawidłowy odnośnik",
                    "Odnośnik jest niekompletny. Poproś o nowy.", ok: false));

            var user = await users.FindByIdAsync(id);
            if (user is null)
                // Nie mówimy „takie konto nie istnieje" — to ta sama informacja co przy wyliczaniu.
                return Html(AuthPages.Message("Odnośnik wygasł",
                    "Ten odnośnik jest nieaktualny. Poproś o nowy na stronie logowania.", ok: false));

            if (user.EmailConfirmed)
                return Html(AuthPages.Message("Adres już potwierdzony", "Możesz się zalogować."));

            var result = await users.ConfirmEmailAsync(user, AuthLinks.DecodeToken(kod));
            return Html(result.Succeeded
                ? AuthPages.Message("Adres potwierdzony", "Konto jest aktywne — możesz się zalogować.")
                : AuthPages.Message("Odnośnik wygasł",
                    $"Odnośnik jest ważny {LinkValidHours} h i działa jeden raz. Poproś o nowy.", ok: false));
        });

        group.MapGet("/potwierdz-email/ponow", (HttpContext http, IAntiforgery af) =>
            Html(AuthPages.ResendConfirmation(TokenField, Token(http, af))));

        group.MapPost("/potwierdz-email/ponow", async (
            HttpContext http, IAntiforgery af, UserManager<AppUserEntity> users, IAppEmailSender mail,
            IOptions<AuthOptions> auth, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (!await Valid(http, af)) return Antiforgery(http, af);

            var form = await http.Request.ReadFormAsync(ct);
            var email = form["email"].ToString().Trim();
            var user = string.IsNullOrWhiteSpace(email) ? null : await users.FindByEmailAsync(email);

            // Wysyłamy tylko, gdy konto istnieje i NIE jest potwierdzone; odpowiedź zawsze ta sama.
            if (user is { EmailConfirmed: false })
                await SendConfirmationAsync(users, mail, logs.CreateLogger("PrawoRAG.Auth"), user, http, auth.Value, ct);

            return Html(AuthPages.ResendConfirmation(TokenField, Token(http, af),
                "Jeśli konto istnieje i czeka na potwierdzenie, wysłaliśmy nowy odnośnik."));
        });

        // --- logowanie / wylogowanie ---------------------------------------------------------

        group.MapGet("/logowanie", (HttpContext http, IAntiforgery af, string? powrot) =>
            Html(AuthPages.Login(TokenField, Token(http, af), null, LocalOrNull(powrot))));

        group.MapPost("/logowanie", async (
            HttpContext http, IAntiforgery af, SignInManager<AppUserEntity> signIn,
            UserManager<AppUserEntity> users, string? powrot, CancellationToken ct) =>
        {
            if (!await Valid(http, af)) return Antiforgery(http, af);

            var form = await http.Request.ReadFormAsync(ct);
            var email = form["email"].ToString().Trim();
            var password = form["password"].ToString();
            var back = LocalOrNull(powrot);

            // Jeden komunikat na wszystkie przypadki (zły adres, złe hasło, brak konta) — inaczej
            // formularz logowania stałby się wyszukiwarką istniejących kont.
            const string Wrong = "Nieprawidłowy adres e-mail lub hasło.";

            // Strop długości PRZED jakimkolwiek hashowaniem — dłuższe niż strop nie może być poprawne,
            // bo rejestracja takich nie przyjmuje, więc odpowiadamy tanio i bez różnicy w komunikacie.
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password)
                || password.Length > MaxPasswordLength)
                return Html(AuthPages.Login(TokenField, Token(http, af), email, back, Wrong));

            var user = await users.FindByEmailAsync(email);
            if (user is null)
            {
                // Kanał czasowy: bez tej weryfikacji odpowiedź „brak konta" wraca o koszt PBKDF2
                // szybciej niż „złe hasło" — i sam stoper zdradza, które adresy mają konta.
                DummyHasher.VerifyHashedPassword(null!, DummyPasswordHash, password);
                return Html(AuthPages.Login(TokenField, Token(http, af), email, back, Wrong));
            }

            var result = await signIn.PasswordSignInAsync(user, password, isPersistent: true, lockoutOnFailure: true);

            if (result.IsLockedOut)
                return Html(AuthPages.Login(TokenField, Token(http, af), email, back,
                    "Konto tymczasowo zablokowane po kilku nieudanych próbach. Spróbuj za kilkanaście minut."));

            if (result.IsNotAllowed)
            {
                // UWAGA: Identity sprawdza „czy wolno się logować" (potwierdzenie adresu) PRZED
                // weryfikacją hasła, więc IsNotAllowed przychodzi też przy BŁĘDNYM haśle. Komunikat
                // „adres niepotwierdzony" wolno pokazać dopiero po dowiedzeniu, że pytający zna hasło —
                // inaczej formularz zdradza istnienie konta każdemu, kto wpisze cokolwiek.
                var knowsPassword = await users.CheckPasswordAsync(user, password);
                return Html(AuthPages.Login(TokenField, Token(http, af), email, back, knowsPassword
                    ? "Adres e-mail nie został jeszcze potwierdzony. Sprawdź skrzynkę albo poproś o nowy odnośnik."
                    : Wrong));
            }

            if (!result.Succeeded)
                return Html(AuthPages.Login(TokenField, Token(http, af), email, back, Wrong));

            return Results.Redirect(back ?? "/czat");
        });

        // GET pokazuje tylko potwierdzenie z formularzem — dzięki temu odnośnik „Wyloguj" w menu
        // może być zwykłym linkiem, a samo wylogowanie i tak dzieje się POST-em z tokenem.
        group.MapGet("/wylogowanie", (HttpContext http, IAntiforgery af) =>
            Html(AuthPages.Logout(TokenField, Token(http, af))));

        // Wylogowanie POST-em (GET dałby się wywołać obrazkiem z obcej strony). Stary /wyjscie
        // zostaje osobno, dla bramki na kody zaproszeń.
        group.MapPost("/wylogowanie", async (HttpContext http, IAntiforgery af, SignInManager<AppUserEntity> signIn) =>
        {
            if (!await Valid(http, af)) return Antiforgery(http, af);
            await signIn.SignOutAsync();
            return Results.Redirect("/");
        });

        // --- reset hasła ---------------------------------------------------------------------

        group.MapGet("/haslo/reset", (HttpContext http, IAntiforgery af) =>
            Html(AuthPages.ResetRequest(TokenField, Token(http, af))));

        group.MapPost("/haslo/reset", async (
            HttpContext http, IAntiforgery af, UserManager<AppUserEntity> users, IAppEmailSender mail,
            IOptions<AuthOptions> auth, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (!await Valid(http, af)) return Antiforgery(http, af);

            var form = await http.Request.ReadFormAsync(ct);
            var email = form["email"].ToString().Trim();
            var user = string.IsNullOrWhiteSpace(email) ? null : await users.FindByEmailAsync(email);

            // Tylko potwierdzone konto dostaje reset: inaczej reset byłby obejściem potwierdzenia adresu.
            if (user is { EmailConfirmed: true })
            {
                var log = logs.CreateLogger("PrawoRAG.Auth");
                var raw = await users.GeneratePasswordResetTokenAsync(user);
                var link = Absolute(http, auth.Value,
                    $"/haslo/nowe?id={Uri.EscapeDataString(user.Id)}&kod={AuthLinks.EncodeToken(raw)}");
                await TrySend(mail, log, user.Email!, EmailTemplates.ResetPassword(
                    AuthPages.ProductName, user.DisplayName, link, LinkValidHours), ct);
                log.LogInformation("Wysłano odnośnik resetu hasła dla konta {UserId}.", user.Id);
            }

            return Html(AuthPages.ResetRequest(TokenField, Token(http, af),
                "Jeśli konto o tym adresie istnieje, wysłaliśmy odnośnik do ustawienia nowego hasła."));
        });

        group.MapGet("/haslo/nowe", (HttpContext http, IAntiforgery af, string? id, string? kod) =>
            string.IsNullOrEmpty(id) || string.IsNullOrEmpty(kod)
                ? Html(AuthPages.Message("Nieprawidłowy odnośnik",
                    "Odnośnik jest niekompletny. Poproś o nowy.", ok: false))
                : Html(AuthPages.ResetForm(TokenField, Token(http, af), id, kod)));

        group.MapPost("/haslo/nowe", async (
            HttpContext http, IAntiforgery af, UserManager<AppUserEntity> users,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            if (!await Valid(http, af)) return Antiforgery(http, af);

            var form = await http.Request.ReadFormAsync(ct);
            var id = form["id"].ToString();
            var kod = form["kod"].ToString();
            var password = form["password"].ToString();
            var password2 = form["password2"].ToString();

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(kod))
                return Html(AuthPages.Message("Nieprawidłowy odnośnik",
                    "Odnośnik jest niekompletny. Poproś o nowy.", ok: false));

            if (password != password2)
                return Html(AuthPages.ResetForm(TokenField, Token(http, af), id, kod,
                    ["Hasła nie są identyczne."]));
            if (password.Length > MaxPasswordLength)
                return Html(AuthPages.ResetForm(TokenField, Token(http, af), id, kod,
                    [$"Hasło może mieć najwyżej {MaxPasswordLength} znaków."]));

            var user = await users.FindByIdAsync(id);
            if (user is null)
                return Html(AuthPages.Message("Odnośnik wygasł",
                    "Ten odnośnik jest nieaktualny. Poproś o nowy.", ok: false));

            var result = await users.ResetPasswordAsync(user, AuthLinks.DecodeToken(kod), password);
            if (!result.Succeeded)
                return Html(AuthPages.ResetForm(TokenField, Token(http, af), id, kod,
                    result.Errors.Select(e => e.Description)));

            // Reset hasła unieważnia istniejące sesje: znacznik bezpieczeństwa zmienia się, więc
            // ciasteczka wydane wcześniej przestają być ważne przy najbliższej walidacji.
            await users.UpdateSecurityStampAsync(user);
            logs.CreateLogger("PrawoRAG.Auth").LogInformation("Zmieniono hasło konta {UserId}.", user.Id);

            return Html(AuthPages.Message("Hasło zmienione", "Możesz zalogować się nowym hasłem."));
        });
    }

    // --- narzędzia ---------------------------------------------------------------------------

    private static IResult Html(string html) => Results.Content(html, "text/html; charset=utf-8");

    private static string MailboxPage() => AuthPages.CheckMailbox("Sprawdź skrzynkę",
        "Jeśli adres jest poprawny, wysłaliśmy na niego odnośnik potwierdzający. " +
        "Zajrzyj też do spamu — to pierwsza wiadomość z tego adresu.");

    private static string Token(HttpContext http, IAntiforgery af) =>
        af.GetAndStoreTokens(http).RequestToken ?? "";

    private static async Task<bool> Valid(HttpContext http, IAntiforgery af)
    {
        try { await af.ValidateRequestAsync(http); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }

    private static IResult Antiforgery(HttpContext http, IAntiforgery af) =>
        Html(AuthPages.Message("Formularz wygasł",
            "Otwórz stronę ponownie i spróbuj jeszcze raz.", ok: false));

    private static async Task SendConfirmationAsync(
        UserManager<AppUserEntity> users, IAppEmailSender mail, ILogger log, AppUserEntity user,
        HttpContext http, AuthOptions auth, CancellationToken ct)
    {
        var raw = await users.GenerateEmailConfirmationTokenAsync(user);
        var link = Absolute(http, auth, $"/potwierdz-email?id={Uri.EscapeDataString(user.Id)}&kod={AuthLinks.EncodeToken(raw)}");
        await TrySend(mail, log, user.Email!, EmailTemplates.ConfirmEmail(
            AuthPages.ProductName, user.DisplayName, link, LinkValidHours), ct);
    }

    /// <summary>
    /// Wysyłka, która nie wywraca żądania. Awaria dostawcy poczty nie może zamienić poprawnej
    /// rejestracji w błąd 500 — konto już istnieje, a użytkownik ma stronę „wyślij ponownie".
    /// Do logu trafia SAM FAKT niepowodzenia, nigdy treść listu (jest w niej token).
    /// </summary>
    private static async Task TrySend(IAppEmailSender mail, ILogger log, string to, EmailMessage msg, CancellationToken ct)
    {
        try { await mail.SendAsync(to, msg, ct); }
        catch (Exception ex) { log.LogError(ex, "Nie udało się wysłać listu transakcyjnego."); }
    }

    // Kodowanie tokenów, budowa adresu i filtr przekierowania siedzą w AuthLinks — są pokryte testami.
    private static string Absolute(HttpContext http, AuthOptions auth, string path) =>
        AuthLinks.Absolute(auth.PublicBaseUrl, http.Request.Scheme, http.Request.Host.Value ?? "", path);

    private static string? LocalOrNull(string? url) => AuthLinks.LocalOrNull(url);

    private static string Trim(string value, int max) =>
        value.Length <= max ? value.Trim() : value[..max].Trim();
}
