using System.Text.Encodings.Web;

namespace PrawoRAG.Api.Services.Auth;

/// <summary>
/// Strony kont renderowane po stronie serwera, BEZ Blazora — świadomie, tak samo jak strona wejścia
/// na kod zaproszenia. Logowanie woła <c>SignInAsync</c>, a to wymaga żywego <c>HttpContext</c>
/// i zapisu ciasteczka przed wysłaniem odpowiedzi; komponent interaktywny działa już po nawiązaniu
/// obwodu SignalR, więc trafiałby na klasyczną pułapkę „nie można zmodyfikować nagłówków".
///
/// Bezpieczeństwo: KAŻDA wartość wstrzykiwana w HTML przechodzi przez <see cref="HtmlEncoder"/>,
/// a każdy formularz niesie token antiforgery (walidowany jawnie w obsłudze POST).
/// Styl bierze się z <c>/css/tokens.css</c>, więc strony kont wyglądają jak reszta aplikacji.
/// </summary>
public static class AuthPages
{
    public const string ProductName = "OmniaSI";

    private static string E(string? v) => HtmlEncoder.Default.Encode(v ?? "");

    /// <summary>Sygnet OmniaSI — jeden gest (pierścień + węzeł), kolory dobrane do tła, bo strona
    /// renderuje się poza Blazorem/tokenami motywu. Wersja jasna na ciemnym hero po lewej.</summary>
    private const string MarkOnDark = """
        <svg class="mark" viewBox="0 0 100 100" aria-hidden="true">
          <path d="M 60.94 19.93 A 32 32 0 1 1 39.06 19.93" fill="none" stroke="#EDEFF8" stroke-width="9" stroke-linecap="butt"/>
          <circle cx="50" cy="18" r="5" fill="#D97706"/>
        </svg>
        """;

    /// <summary>Wersja granatowa na białej karcie (widoczna tylko poniżej 900px, gdy hero znika).</summary>
    private const string MarkOnLight = """
        <svg class="mark" viewBox="0 0 100 100" aria-hidden="true">
          <path d="M 60.94 19.93 A 32 32 0 1 1 39.06 19.93" fill="none" stroke="#142450" stroke-width="9" stroke-linecap="butt"/>
          <circle cx="50" cy="18" r="5" fill="#D97706"/>
        </svg>
        """;

    /// <summary>
    /// Wspólna oprawa (RED-4.1): split-layout z makiety — lewa połowa to ciemny gradient z marką
    /// i obietnicą, prawa to karta formularza. Poniżej 900px lewa połowa znika (marka przechodzi
    /// nad kartę). Literał jest `$$"""` (podwójny `$`), bo w środku jest CSS — pojedyncza klamra
    /// musi zostać klamrą, a interpolacje idą przez `{{ }}`. Formularze, tokeny antiforgery
    /// i komunikaty konkretnych stron wchodzą w `bodyHtml` bez żadnych zmian.
    /// </summary>
    public static string Page(string title, string bodyHtml) => $$"""
        <!doctype html>
        <html lang="pl">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <meta name="robots" content="noindex">
        <title>{{E(ProductName)}} — {{E(title)}}</title>
        <link rel="stylesheet" href="/css/tokens.css">
        <style>
          body{font-family:var(--sl-font-base);background:var(--sl-bg);color:var(--sl-text-primary);margin:0;
               line-height:var(--lh-body);display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);min-height:100vh}
          .auth-side{background:var(--sl-hero-gradient);color:var(--sl-on-dark);
                     display:flex;flex-direction:column;justify-content:space-between;padding:var(--s-12) var(--s-10)}
          .auth-side .auth-brand{color:#EDEFF8}
          .auth-side .auth-brand .si{color:#93B4FF}
          .auth-promise{max-width:30rem;display:flex;flex-direction:column;gap:var(--s-5)}
          .auth-claim{font-family:var(--sl-font-display);font-size:var(--fs-32);line-height:1.2;font-weight:700;letter-spacing:-0.01em}
          .auth-points{display:flex;flex-direction:column;gap:var(--s-3);font-size:var(--fs-15);color:var(--sl-on-dark-soft)}
          .auth-points span::before{content:"\2713\0020";color:var(--sl-on-dark-accent);font-weight:700}
          .auth-foot{font-size:var(--fs-12);color:var(--sl-on-dark-faint)}
          .auth-wrap{display:flex;justify-content:center;align-items:center;padding:var(--s-6)}
          .auth{background:var(--sl-surface);border-radius:var(--sl-radius-xl);
                box-shadow:var(--sl-shadow-card);width:100%;max-width:27rem;padding:var(--s-10) var(--s-10) var(--s-8)}
          .auth-brand{display:flex;align-items:center;gap:6px;margin-bottom:var(--s-6);
                      text-decoration:none;color:#0A0A0A;line-height:1;
                      font-family:var(--sl-font-base);font-size:var(--fs-20);letter-spacing:-0.01em}
          .auth-brand .omnia{font-weight:700}
          .auth-brand .si{font-weight:400;color:#2563EB}
          .auth-brand .mark{width:30px;height:30px;display:block;flex:none}
          .auth .auth-brand{display:none}
          h1{font-family:var(--sl-font-display);font-size:var(--fs-24);letter-spacing:-0.01em;margin:0 0 var(--s-2)}
          p{margin:0 0 var(--s-4);color:var(--sl-text-secondary);font-size:var(--fs-14)}
          label{display:block;font-size:var(--fs-13);font-weight:600;margin:var(--s-4) 0 var(--s-2)}
          input[type=email],input[type=password],input[type=text]{
            width:100%;min-height:44px;padding:.6rem .85rem;border:1.5px solid var(--sl-border);border-radius:var(--sl-radius-lg);
            font-size:var(--fs-16);font-family:inherit;background:var(--sl-bg);color:var(--sl-text-primary);box-sizing:border-box}
          input:focus{outline:none;border-color:var(--sl-accent);background:var(--sl-surface);box-shadow:var(--sl-focus-ring)}
          .hint{font-size:var(--fs-12);color:var(--sl-text-secondary);margin:var(--s-1) 0 0}
          .check{display:flex;gap:var(--s-2);align-items:flex-start;margin:var(--s-4) 0 0;font-size:var(--fs-14)}
          .check input{margin-top:.25rem}
          button{width:100%;margin-top:var(--s-6);min-height:48px;padding:.7rem;border:0;border-radius:var(--sl-radius-lg);
                 background:var(--sl-gradient);color:var(--sl-text-inverse);font-size:var(--fs-16);font-weight:700;
                 cursor:pointer;font-family:inherit;box-shadow:var(--sl-shadow-accent)}
          button:hover{filter:brightness(1.07)}
          button:active{transform:translateY(1px)}
          button:focus-visible{outline:none;box-shadow:var(--sl-focus-ring),var(--sl-shadow-accent)}
          .alert{padding:var(--s-3) var(--s-4);border-radius:var(--sl-radius-lg);font-size:var(--fs-14);margin:0 0 var(--s-4)}
          .alert-error{background:var(--sl-error-bg);color:var(--sl-error)}
          .alert-ok{background:var(--sl-success-bg);color:var(--sl-success)}
          .alert ul{margin:var(--s-1) 0 0;padding-left:1.1rem}
          .links{margin-top:var(--s-6);padding-top:var(--s-4);border-top:1px solid var(--sl-border);
                 font-size:var(--fs-14);display:flex;flex-direction:column;gap:var(--s-2)}
          a{color:var(--sl-accent)}a:hover{color:var(--sl-accent-hover)}
          .note{font-size:var(--fs-12);color:var(--sl-text-tertiary);margin-top:var(--s-4)}
          @media (max-width:900px){
            body{grid-template-columns:minmax(0,1fr)}
            .auth-side{display:none}
            .auth .auth-brand{display:flex}
          }
        </style>
        </head>
        <body>
        <aside class="auth-side">
          <a class="auth-brand" href="/">{{MarkOnDark}}<span class="omnia">Omnia</span><span class="si">SI</span></a>
          <div class="auth-promise">
            <div class="auth-claim">Research prawny na źródłach, nie na domysłach.</div>
            <div class="auth-points">
              <span>Odpowiedzi wyłącznie z przepisów i orzecznictwa, z cytowaniami do weryfikacji</span>
              <span>Uczciwa odmowa, gdy źródła nie pozwalają odpowiedzieć</span>
              <span>Twoje pytania i dokumenty nie trenują żadnego modelu</span>
            </div>
          </div>
          <div class="auth-foot">&copy; {{E(ProductName)}} &middot; <a href="/regulamin" style="color:inherit">Regulamin</a> &middot; <a href="/prywatnosc" style="color:inherit">Polityka prywatności</a></div>
        </aside>
        <div class="auth-wrap">
        <main class="auth">
          <a class="auth-brand" href="/">{{MarkOnLight}}<span class="omnia">Omnia</span><span class="si">SI</span></a>
          {{bodyHtml}}
        </main>
        </div>
        {{AnalyticsSnippet.Html}}
        </body>
        </html>
        """;

    /// <summary>Pole ukryte z tokenem antiforgery — w każdym formularzu.</summary>
    public static string Token(string fieldName, string token) =>
        $"""<input type="hidden" name="{E(fieldName)}" value="{E(token)}">""";

    public static string Error(string message) =>
        $"""<div class="alert alert-error">{E(message)}</div>""";

    /// <summary>Lista błędów walidacji (np. wymagania hasła) — pozycje kodowane pojedynczo.</summary>
    public static string Errors(IEnumerable<string> messages)
    {
        var items = string.Concat(messages.Select(m => $"<li>{E(m)}</li>"));
        return items.Length == 0 ? "" : $"""<div class="alert alert-error"><ul>{items}</ul></div>""";
    }

    public static string Ok(string message) =>
        $"""<div class="alert alert-ok">{E(message)}</div>""";

    // --- konkretne strony -------------------------------------------------------------------

    public static string Register(string tokenField, string token, string? email, string? displayName,
        IEnumerable<string>? errors = null, bool marketing = false) => Page("rejestracja", $"""
        <h1>Załóż konto</h1>
        <p>Dostęp do asystenta prawnego z klikalnymi cytatami.</p>
        {Errors(errors ?? [])}
        <form method="post" action="/rejestracja" autocomplete="on">
          {Token(tokenField, token)}
          <label for="email">Adres e-mail</label>
          <input id="email" name="email" type="email" autocomplete="email" required maxlength="254" value="{E(email)}" autofocus>
          <label for="displayName">Imię i nazwisko <span style="font-weight:400">(opcjonalnie)</span></label>
          <input id="displayName" name="displayName" type="text" autocomplete="name" maxlength="200" value="{E(displayName)}">
          <label for="password">Hasło</label>
          <input id="password" name="password" type="password" autocomplete="new-password" required minlength="10" maxlength="256">
          <p class="hint">Co najmniej 10 znaków. Długość chroni lepiej niż znaki specjalne — najlepiej fraza.</p>
          <label class="check" for="terms">
            <input id="terms" name="terms" type="checkbox" value="tak" required>
            <span>Akceptuję <a href="/regulamin" target="_blank" rel="noopener">regulamin</a> i
            <a href="/prywatnosc" target="_blank" rel="noopener">politykę prywatności</a>.</span>
          </label>
          <label class="check" for="marketing">
            <input id="marketing" name="marketing" type="checkbox" value="tak"{(marketing ? " checked" : "")}>
            <span>Chcę otrzymywać informacje o nowościach i ofertach {E(ProductName)}
            <span style="font-weight:400">(opcjonalnie — zgodę można wycofać w każdej chwili na stronie konta)</span>.</span>
          </label>
          <button type="submit" data-umami-event="rejestracja-submit">Załóż konto</button>
        </form>
        <div class="links">
          <span>Masz już konto? <a href="/logowanie">Zaloguj się</a></span>
        </div>
        <p class="note">To wstępny research prawny do weryfikacji, nie porada prawna.</p>
        """);

    public static string Login(string tokenField, string token, string? email, string? returnUrl,
        string? error = null, string? notice = null) => Page("logowanie", $"""
        <h1>Zaloguj się</h1>
        {(notice is null ? "" : Ok(notice))}
        {(error is null ? "" : Error(error))}
        <form method="post" action="/logowanie{(string.IsNullOrEmpty(returnUrl) ? "" : $"?powrot={Uri.EscapeDataString(returnUrl)}")}" autocomplete="on">
          {Token(tokenField, token)}
          <label for="email">Adres e-mail</label>
          <input id="email" name="email" type="email" autocomplete="email" required maxlength="254" value="{E(email)}" autofocus>
          <label for="password">Hasło</label>
          <input id="password" name="password" type="password" autocomplete="current-password" required maxlength="256">
          <button type="submit">Zaloguj</button>
        </form>
        <div class="links">
          <a href="/haslo/reset">Nie pamiętam hasła</a>
          <span>Nie masz konta? <a href="/rejestracja">Załóż konto</a></span>
        </div>
        """);

    public static string CheckMailbox(string heading, string body) => Page(heading, $"""
        <h1>{E(heading)}</h1>
        <p>{E(body)}</p>
        <div class="links">
          <a href="/logowanie">Wróć do logowania</a>
          <a href="/potwierdz-email/ponow">Wyślij ponownie link potwierdzający</a>
        </div>
        """);

    public static string ResendConfirmation(string tokenField, string token, string? notice = null) =>
        Page("ponowna wysyłka", $"""
        <h1>Wyślij link ponownie</h1>
        {(notice is null ? "" : Ok(notice))}
        <p>Podaj adres użyty przy rejestracji. Jeśli konto istnieje i nie jest jeszcze potwierdzone, wyślemy nowy link.</p>
        <form method="post" action="/potwierdz-email/ponow">
          {Token(tokenField, token)}
          <label for="email">Adres e-mail</label>
          <input id="email" name="email" type="email" autocomplete="email" required maxlength="254" autofocus>
          <button type="submit">Wyślij link</button>
        </form>
        <div class="links"><a href="/logowanie">Wróć do logowania</a></div>
        """);

    public static string ResetRequest(string tokenField, string token, string? notice = null) =>
        Page("reset hasła", $"""
        <h1>Nie pamiętam hasła</h1>
        {(notice is null ? "" : Ok(notice))}
        <p>Podaj adres e-mail konta. Jeśli takie konto istnieje, wyślemy odnośnik do ustawienia nowego hasła.</p>
        <form method="post" action="/haslo/reset">
          {Token(tokenField, token)}
          <label for="email">Adres e-mail</label>
          <input id="email" name="email" type="email" autocomplete="email" required maxlength="254" autofocus>
          <button type="submit">Wyślij odnośnik</button>
        </form>
        <div class="links"><a href="/logowanie">Wróć do logowania</a></div>
        """);

    public static string ResetForm(string tokenField, string token, string userId, string code,
        IEnumerable<string>? errors = null) => Page("nowe hasło", $"""
        <h1>Ustaw nowe hasło</h1>
        {Errors(errors ?? [])}
        <form method="post" action="/haslo/nowe" autocomplete="on">
          {Token(tokenField, token)}
          <input type="hidden" name="id" value="{E(userId)}">
          <input type="hidden" name="kod" value="{E(code)}">
          <label for="password">Nowe hasło</label>
          <input id="password" name="password" type="password" autocomplete="new-password" required minlength="10" maxlength="256" autofocus>
          <p class="hint">Co najmniej 10 znaków.</p>
          <label for="password2">Powtórz hasło</label>
          <input id="password2" name="password2" type="password" autocomplete="new-password" required minlength="10" maxlength="256">
          <button type="submit">Zapisz hasło</button>
        </form>
        """);

    /// <summary>Potwierdzenie wylogowania — samo wylogowanie idzie POST-em z tokenem.</summary>
    public static string Logout(string tokenField, string token) => Page("wylogowanie", $"""
        <h1>Wylogować się?</h1>
        <p>Sesja zostanie zamknięta na tym urządzeniu.</p>
        <form method="post" action="/wylogowanie">
          {Token(tokenField, token)}
          <button type="submit">Wyloguj</button>
        </form>
        <div class="links"><a href="/czat">Wróć do aplikacji</a></div>
        """);

    /// <summary>Komunikat końcowy (potwierdzony adres, zmienione hasło, wygasły odnośnik).</summary>
    public static string Message(string heading, string body, bool ok = true) => Page(heading, $"""
        <h1>{E(heading)}</h1>
        {(ok ? Ok(body) : Error(body))}
        <div class="links"><a href="/logowanie">Przejdź do logowania</a></div>
        """);
}
