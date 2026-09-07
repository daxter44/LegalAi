using System.Text.Encodings.Web;

namespace PrawoRAG.Api.Services.Auth;

/// <summary>Gotowy list: temat + wersja HTML + wersja tekstowa (klienci bez HTML i filtry antyspamowe).</summary>
public sealed record EmailMessage(string Subject, string Html, string Text);

/// <summary>
/// Szablony poczty transakcyjnej. Zasady:
/// • styl INLINE — klienty pocztowe wycinają arkusze i większość selektorów;
/// • układ na tabeli — Outlook nie renderuje flexboksa;
/// • każdy przycisk ma pod spodem surowy odnośnik (część klientów blokuje przyciski);
/// • KAŻDA wartość od użytkownika przechodzi przez <see cref="HtmlEncoder"/> — treść listu składamy
///   z interpolacji, więc niezakodowana nazwa konta byłaby wstrzyknięciem HTML do cudzej skrzynki.
/// Kolory zgodne z tokenami interfejsu (wwwroot/css/tokens.css).
/// </summary>
public static class EmailTemplates
{
    private const string Accent = "#1f3a8a";
    private const string Text = "#1a1f2b";
    private const string Muted = "#5b6472";
    private const string Border = "#e2e5ea";
    private const string Bg = "#f7f8fa";

    private static string E(string? value) => HtmlEncoder.Default.Encode(value ?? "");

    /// <summary>Potwierdzenie adresu e-mail po rejestracji.</summary>
    public static EmailMessage ConfirmEmail(string productName, string? displayName, string link, int validHours)
    {
        var hello = Greeting(displayName);
        var html = Layout(productName,
            heading: "Potwierdź swój adres e-mail",
            bodyHtml: $"""
                <p style="margin:0 0 16px">{hello}</p>
                <p style="margin:0 0 16px">Konto w serwisie {E(productName)} zostało założone dla tego adresu.
                Potwierdź go, żeby zacząć korzystać z asystenta.</p>
                """,
            buttonLabel: "Potwierdzam adres",
            buttonUrl: link,
            footnoteHtml: $"""
                <p style="margin:0 0 8px">Odnośnik jest ważny {validHours} h i działa jeden raz.</p>
                <p style="margin:0">Jeśli to nie Ty zakładałeś konto, zignoruj tę wiadomość — bez potwierdzenia
                konto pozostaje nieaktywne.</p>
                """);

        var text = $"""
            {Plain(hello)}

            Konto w serwisie {productName} zostało założone dla tego adresu. Potwierdź go, otwierając odnośnik:

            {link}

            Odnośnik jest ważny {validHours} h i działa jeden raz.
            Jeśli to nie Ty zakładałeś konto, zignoruj tę wiadomość — bez potwierdzenia konto pozostaje nieaktywne.
            """;

        return new EmailMessage($"{productName} — potwierdź adres e-mail", html, text);
    }

    /// <summary>Reset hasła. Wysyłany tylko na adres istniejącego, potwierdzonego konta.</summary>
    public static EmailMessage ResetPassword(string productName, string? displayName, string link, int validHours)
    {
        var hello = Greeting(displayName);
        var html = Layout(productName,
            heading: "Ustaw nowe hasło",
            bodyHtml: $"""
                <p style="margin:0 0 16px">{hello}</p>
                <p style="margin:0 0 16px">Ktoś poprosił o zresetowanie hasła do konta w serwisie {E(productName)}.
                Jeśli to Ty — ustaw nowe hasło:</p>
                """,
            buttonLabel: "Ustawiam nowe hasło",
            buttonUrl: link,
            footnoteHtml: $"""
                <p style="margin:0 0 8px">Odnośnik jest ważny {validHours} h i działa jeden raz.</p>
                <p style="margin:0">Jeśli to nie Ty — nic nie rób. Hasło pozostaje bez zmian, a odnośnik wygaśnie sam.</p>
                """);

        var text = $"""
            {Plain(hello)}

            Ktoś poprosił o zresetowanie hasła do konta w serwisie {productName}. Jeśli to Ty, otwórz odnośnik:

            {link}

            Odnośnik jest ważny {validHours} h i działa jeden raz.
            Jeśli to nie Ty — nic nie rób. Hasło pozostaje bez zmian.
            """;

        return new EmailMessage($"{productName} — reset hasła", html, text);
    }

    /// <summary>
    /// Próba rejestracji na adres, który JUŻ ma konto. Wysyłane zamiast komunikatu na stronie —
    /// formularz rejestracji nigdy nie zdradza, czy dany adres istnieje w bazie (ochrona przed
    /// wyliczaniem kont), więc informację dostaje wyłącznie właściciel skrzynki.
    /// </summary>
    public static EmailMessage AccountAlreadyExists(string productName, string? displayName, string loginUrl, string resetUrl)
    {
        var hello = Greeting(displayName);
        var html = Layout(productName,
            heading: "Konto na ten adres już istnieje",
            bodyHtml: $"""
                <p style="margin:0 0 16px">{hello}</p>
                <p style="margin:0 0 16px">Ktoś (być może Ty) próbował założyć konto w serwisie {E(productName)}
                na ten adres. Konto już istnieje, więc nie zakładaliśmy nowego.</p>
                """,
            buttonLabel: "Przejdź do logowania",
            buttonUrl: loginUrl,
            footnoteHtml: $"""
                <p style="margin:0 0 8px">Nie pamiętasz hasła? <a href="{E(resetUrl)}" style="color:{Accent}">Ustaw nowe</a>.</p>
                <p style="margin:0">Jeśli to nie Ty — nic nie rób. Nikt nie uzyskał dostępu do konta.</p>
                """);

        var text = $"""
            {Plain(hello)}

            Ktoś próbował założyć konto w serwisie {productName} na ten adres. Konto już istnieje, więc nie
            zakładaliśmy nowego.

            Logowanie: {loginUrl}
            Nie pamiętasz hasła: {resetUrl}

            Jeśli to nie Ty — nic nie rób. Nikt nie uzyskał dostępu do konta.
            """;

        return new EmailMessage($"{productName} — próba rejestracji na istniejący adres", html, text);
    }

    private static string Greeting(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? "Dzień dobry," : $"Dzień dobry, {E(displayName)},";

    private static string Plain(string encodedHtml) =>
        System.Net.WebUtility.HtmlDecode(encodedHtml);

    /// <summary>Wspólna oprawa listu — nagłówek z marką, treść, przycisk, stopka z zastrzeżeniem.</summary>
    private static string Layout(string productName, string heading, string bodyHtml,
        string buttonLabel, string buttonUrl, string footnoteHtml) => $"""
        <!doctype html>
        <html lang="pl">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>{E(heading)}</title>
        </head>
        <body style="margin:0;padding:0;background:{Bg};color:{Text};font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Arial,sans-serif;line-height:1.6">
        <span style="display:none;font-size:1px;color:{Bg};max-height:0;overflow:hidden">{E(heading)} — {E(productName)}</span>
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:{Bg};padding:24px 12px">
          <tr>
            <td align="center">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:520px;background:#ffffff;border:1px solid {Border};border-radius:10px">
                <tr>
                  <td style="padding:20px 28px;border-bottom:1px solid {Border}">
                    <span style="color:{Accent};font-size:20px;font-weight:700;vertical-align:middle">&sect;</span>
                    <span style="font-size:16px;font-weight:600;margin-left:6px;vertical-align:middle">{E(productName)}</span>
                  </td>
                </tr>
                <tr>
                  <td style="padding:28px">
                    <h1 style="margin:0 0 16px;font-size:20px;line-height:1.3">{E(heading)}</h1>
                    {bodyHtml}
                    <table role="presentation" cellpadding="0" cellspacing="0" style="margin:24px 0">
                      <tr>
                        <td style="background:{Accent};border-radius:8px">
                          <a href="{E(buttonUrl)}" style="display:inline-block;padding:12px 22px;color:#ffffff;text-decoration:none;font-weight:600;font-size:15px">{E(buttonLabel)}</a>
                        </td>
                      </tr>
                    </table>
                    <p style="margin:0 0 6px;font-size:13px;color:{Muted}">Przycisk nie działa? Skopiuj ten odnośnik do przeglądarki:</p>
                    <p style="margin:0 0 24px;font-size:13px;word-break:break-all"><a href="{E(buttonUrl)}" style="color:{Accent}">{E(buttonUrl)}</a></p>
                    <div style="font-size:13px;color:{Muted};border-top:1px solid {Border};padding-top:16px">
                      {footnoteHtml}
                    </div>
                  </td>
                </tr>
                <tr>
                  <td style="padding:16px 28px;border-top:1px solid {Border};font-size:12px;color:{Muted}">
                    {E(productName)} to wstępny research prawny do weryfikacji, nie porada prawna.
                    Tej wiadomości nie trzeba odpisywać.
                  </td>
                </tr>
              </table>
            </td>
          </tr>
        </table>
        </body>
        </html>
        """;
}
