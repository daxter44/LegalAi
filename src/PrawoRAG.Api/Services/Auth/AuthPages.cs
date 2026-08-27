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
    public const string ProductName = "PrawoRAG";

    private static string E(string? v) => HtmlEncoder.Default.Encode(v ?? "");

    /// <summary>
    /// Wspólna oprawa: wyśrodkowana karta z marką na górze. Literał jest `$$"""` (podwójny `$`),
    /// bo w środku jest CSS — pojedyncza klamra musi zostać klamrą, a interpolacje idą przez `{{ }}`.
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
          body{font-family:var(--font-sans);background:var(--c-bg);color:var(--c-text);margin:0;
               line-height:var(--lh-body);display:flex;justify-content:center;align-items:center;min-height:100vh;padding:var(--s-4)}
          .auth{background:var(--c-surface);border:1px solid var(--c-border);border-radius:var(--radius);
                box-shadow:var(--shadow);width:100%;max-width:26rem;padding:var(--s-8) var(--s-8) var(--s-6)}
          .auth-brand{display:flex;align-items:center;gap:var(--s-2);margin-bottom:var(--s-6);
                      font-weight:600;text-decoration:none;color:inherit}
          .auth-brand span.logo{color:var(--c-accent);font-size:var(--fs-24);line-height:1}
          h1{font-size:var(--fs-18);margin:0 0 var(--s-2)}
          p{margin:0 0 var(--s-4);color:var(--c-text-muted);font-size:var(--fs-14)}
          label{display:block;font-size:var(--fs-14);font-weight:600;margin:var(--s-4) 0 var(--s-1)}
          input[type=email],input[type=password],input[type=text]{
            width:100%;padding:.6rem .7rem;border:1px solid var(--c-border);border-radius:var(--radius-sm);
            font-size:var(--fs-16);font-family:inherit;background:var(--c-surface);color:var(--c-text);box-sizing:border-box}
          input:focus{outline:none;border-color:var(--c-accent);box-shadow:var(--focus)}
          .hint{font-size:var(--fs-12);color:var(--c-text-muted);margin:var(--s-1) 0 0}
          .check{display:flex;gap:var(--s-2);align-items:flex-start;margin:var(--s-4) 0 0;font-size:var(--fs-14)}
          .check input{margin-top:.25rem}
          button{width:100%;margin-top:var(--s-6);padding:.7rem;border:0;border-radius:var(--radius-sm);
                 background:var(--c-accent);color:#fff;font-size:var(--fs-16);font-weight:600;cursor:pointer;font-family:inherit}
          button:hover{filter:brightness(1.08)}
          button:focus-visible{outline:none;box-shadow:var(--focus)}
          .alert{padding:var(--s-3);border-radius:var(--radius-sm);font-size:var(--fs-14);margin:0 0 var(--s-4)}
          .alert-error{background:var(--c-danger-weak);color:var(--c-danger)}
          .alert-ok{background:var(--c-ok-weak);color:var(--c-ok)}
          .alert ul{margin:var(--s-1) 0 0;padding-left:1.1rem}
          .links{margin-top:var(--s-6);padding-top:var(--s-4);border-top:1px solid var(--c-border);
                 font-size:var(--fs-14);display:flex;flex-direction:column;gap:var(--s-2)}
          a{color:var(--c-accent)}
          .note{font-size:var(--fs-12);color:var(--c-text-muted);margin-top:var(--s-4)}
        </style>
        </head>
        <body>
        <main class="auth">
          <a class="auth-brand" href="/"><span class="logo">&sect;</span> {{E(ProductName)}}</a>
          {{bodyHtml}}
        </main>
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
        IEnumerable<string>? errors = null) => Page("rejestracja", $"""
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
          <button type="submit">Załóż konto</button>
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
