using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using PrawoRAG.Api.Services;
using PrawoRAG.Api.Services.Auth;

namespace PrawoRAG.Tests.Access;

/// <summary>
/// Konta (E1, blok A) — testy tego, co przy pomyłce kosztuje najwięcej, a da się sprawdzić bez HTTP
/// i bez bazy: filtr przekierowań (open redirect), kodowanie tokenów z odnośników, budowa adresu
/// bazowego listu, kodowanie HTML na stronach i w e-mailach oraz kolejność źródeł tożsamości.
/// </summary>
public class AuthTests
{
    // --- AuthLinks.LocalOrNull: przekierowanie po zalogowaniu ---

    [Theory]
    [InlineData("/czat")]
    [InlineData("/dokument/123")]
    [InlineData("/szukaj?q=abc")]
    public void Local_return_url_is_allowed(string url) =>
        Assert.Equal(url, AuthLinks.LocalOrNull(url));

    [Theory]
    [InlineData("//zly.example/phishing")]   // przeglądarka widzi to jak adres zewnętrzny
    [InlineData("/\\zly.example")]           // wariant z ukośnikiem wstecznym
    [InlineData("https://zly.example")]
    [InlineData("http://zly.example")]
    [InlineData("czat")]                     // bez wiodącego ukośnika
    [InlineData("")]
    [InlineData(null)]
    public void External_return_url_is_rejected(string? url) =>
        Assert.Null(AuthLinks.LocalOrNull(url));

    // --- AuthLinks: tokeny z odnośników ---

    [Fact]
    public void Token_survives_encoding_round_trip()
    {
        // Token Identity zawiera „+", „/" i „=" — bez base64url rozjeżdża się w adresie.
        const string raw = "CfDJ8Ab+cd/ef==zażółć gęślą jaźń";

        var encoded = AuthLinks.EncodeToken(raw);

        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
        Assert.Equal(raw, AuthLinks.DecodeToken(encoded));
    }

    [Theory]
    [InlineData("!!!nie-base64!!!")]
    [InlineData("")]
    [InlineData(null)]
    public void Broken_token_decodes_to_empty_instead_of_throwing(string? code) =>
        Assert.Equal("", AuthLinks.DecodeToken(code));

    // --- AuthLinks.Absolute: adres w liście ---

    [Fact]
    public void Configured_base_url_wins_over_request_host()
    {
        // Host z żądania pochodzi z nagłówka sterowanego przez klienta — w produkcji nie ufamy mu.
        var link = AuthLinks.Absolute("https://praworag.pl/", "http", "podrobiony.example", "/haslo/nowe?id=1");

        Assert.Equal("https://praworag.pl/haslo/nowe?id=1", link);
    }

    [Fact]
    public void Without_configured_base_url_request_is_used()
    {
        var link = AuthLinks.Absolute("", "https", "localhost:5024", "/potwierdz-email?id=1");

        Assert.Equal("https://localhost:5024/potwierdz-email?id=1", link);
    }

    // --- kodowanie HTML: strony ---

    [Fact]
    public void Login_page_encodes_user_supplied_email()
    {
        // Adres wraca na stronę po nieudanej próbie — niezakodowany byłby wstrzyknięciem skryptu.
        var html = AuthPages.Login("__token", "abc", "\"><script>alert(1)</script>", null, "Błąd");

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact] // Zgoda marketingowa: checkbox OPCJONALNY, domyślnie odznaczony; stan przeżywa re-render błędu.
    public void Register_page_has_optional_unchecked_marketing_checkbox()
    {
        var html = AuthPages.Register("__token", "tok", null, null);
        Assert.Contains("name=\"marketing\"", html);
        Assert.DoesNotContain("name=\"marketing\" type=\"checkbox\" value=\"tak\" checked", html);
        Assert.DoesNotContain("name=\"marketing\" type=\"checkbox\" value=\"tak\" required", html); // opcjonalna

        var rerender = AuthPages.Register("__token", "tok", "a@b.pl", null, ["błąd"], marketing: true);
        Assert.Contains("name=\"marketing\" type=\"checkbox\" value=\"tak\" checked", rerender);
    }

    [Fact] // Analityka: snippet pusty bez konfiguracji; z konfiguracją = consent.js + identyfikatory.
    public void Analytics_snippet_follows_configuration()
    {
        try
        {
            AnalyticsSnippet.Configure(new AnalyticsOptions());
            Assert.Equal("", AnalyticsSnippet.Html);

            AnalyticsSnippet.Configure(new AnalyticsOptions { ClarityProjectId = "cl1", GaMeasurementId = "G-XYZ" });
            Assert.Contains("/js/consent.js", AnalyticsSnippet.Html);
            Assert.Contains("data-clarity=\"cl1\"", AnalyticsSnippet.Html);
            Assert.Contains("data-ga=\"G-XYZ\"", AnalyticsSnippet.Html);
        }
        finally
        {
            AnalyticsSnippet.Configure(new AnalyticsOptions()); // stan globalny — sprzątamy po teście
        }
    }

    [Fact]
    public void Register_page_encodes_validation_messages_and_carries_token()
    {
        var html = AuthPages.Register("__token", "tok123", "a@b.pl", "<b>Jan</b>", ["<i>błąd</i>"]);

        Assert.Contains("""name="__token" value="tok123" """.TrimEnd(), html);
        Assert.DoesNotContain("<b>Jan</b>", html);
        Assert.DoesNotContain("<i>błąd</i>", html);
        Assert.Contains("&lt;i&gt;", html);
    }

    // --- kodowanie HTML: e-maile ---

    [Fact]
    public void Confirmation_email_encodes_display_name_and_carries_link()
    {
        var link = "https://praworag.pl/potwierdz-email?id=1&kod=abc";

        var msg = EmailTemplates.ConfirmEmail("PrawoRAG", "<img src=x onerror=alert(1)>", link, 6);

        Assert.DoesNotContain("<img src=x", msg.Html);      // nazwa konta nie może być HTML-em
        Assert.Contains("&lt;img", msg.Html);
        Assert.Contains("&amp;kod=abc", msg.Html);          // adres zakodowany w atrybucie href
        Assert.Contains(link, msg.Text);                    // wersja tekstowa ma surowy odnośnik
        Assert.Contains("6 h", msg.Text);
    }

    [Fact]
    public void Reset_email_has_subject_html_and_text()
    {
        var msg = EmailTemplates.ResetPassword("PrawoRAG", "Jan Kowalski", "https://praworag.pl/haslo/nowe", 6);

        Assert.Contains("reset hasła", msg.Subject);
        Assert.Contains("Jan Kowalski", msg.Html);
        Assert.False(string.IsNullOrWhiteSpace(msg.Text));
        Assert.DoesNotContain("<html", msg.Text);           // tekstowa naprawdę jest tekstowa
    }

    // --- tożsamość: kolejność źródeł (T-3) ---

    private static ICurrentUser UserFrom(params Claim[] claims)
    {
        var ctx = new DefaultHttpContext
        {
            User = claims.Length == 0
                ? new ClaimsPrincipal(new ClaimsIdentity())
                : new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
        };
        return new CurrentUser(new HttpContextAccessor { HttpContext = ctx });
    }

    [Fact]
    public void Identity_is_account_id_not_email()
    {
        // Sedno T-3: zmiana adresu e-mail NIE może zmienić klucza, pod którym leżą rozmowy.
        var user = UserFrom(
            new Claim(ClaimTypes.NameIdentifier, "9d1b0c7e-1111-2222-3333-444455556666"),
            new Claim(ClaimTypes.Email, "jan@kancelaria.pl"),
            new Claim(ClaimTypes.Name, "jan@kancelaria.pl"));

        Assert.Equal("9d1b0c7e-1111-2222-3333-444455556666", user.UserId);
        Assert.Equal("jan@kancelaria.pl", user.DisplayName);
        Assert.True(user.IsAuthenticated);
    }

    [Fact]
    public void Invite_gate_identity_still_works()
    {
        // Stara bramka na kody zaproszeń nie wystawia identyfikatora konta — tożsamością zostaje nazwa.
        var user = UserFrom(new Claim(ClaimTypes.Name, "Jan Kowalski"));

        Assert.Equal("Jan Kowalski", user.UserId);
    }

    [Fact]
    public void Anonymous_falls_back_to_dev_placeholder()
    {
        var user = UserFrom();

        Assert.Equal("demo@local", user.UserId);
        Assert.False(user.IsAuthenticated);
    }
}
