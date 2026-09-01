using System.Text.Encodings.Web;

namespace PrawoRAG.Api.Services;

/// <summary>
/// Analityka (Microsoft Clarity + Google Analytics) — WYŁĄCZNIE za zgodą użytkownika. Puste
/// identyfikatory (dev/domyślnie) = analityka wyłączona: zero skryptów, zero banera cookie,
/// zero rozszerzeń CSP. Uwaga treściowa (do polityki prywatności, blok treści): Clarity/GA to
/// usługi amerykańskie — obietnica „dane i modele w UE" dotyczy TREŚCI pytań i dokumentów;
/// dane analityczne trzeba w polityce jawnie rozdzielić.
/// </summary>
public sealed class AnalyticsOptions
{
    public const string SectionName = "Analytics";

    /// <summary>Identyfikator projektu Microsoft Clarity (pusty = wyłączone).</summary>
    public string? ClarityProjectId { get; set; }

    /// <summary>Measurement ID Google Analytics 4, np. „G-XXXXXXX" (pusty = wyłączone).</summary>
    public string? GaMeasurementId { get; set; }

    public bool Enabled =>
        !string.IsNullOrWhiteSpace(ClarityProjectId) || !string.IsNullOrWhiteSpace(GaMeasurementId);
}

/// <summary>
/// Wpięcie skryptu zgody na cookies do WSZYSTKICH powłok HTML aplikacji (Blazor App.razor, landing,
/// strony kont, /konto, placeholdery prawne). Statyczne — bo połowa powłok to statyczne buildery
/// stringów bez dostępu do DI; konfigurowane RAZ na starcie w Program.cs.
///
/// Mechanika zgodności (ePrivacy/RODO): sam consent.js jest first-party i nie stawia żadnych
/// cookies poza zapamiętaniem wyboru; skrypty Clarity/GA ładuje DOPIERO po zgodzie „analityczne"
/// (wybór trwały, wycofywalny — baner ponownie otwiera każdy element o id="cookie-settings").
/// CSP: hosty analityki dokładane są do nagłówka tylko, gdy analityka jest skonfigurowana.
/// </summary>
public static class AnalyticsSnippet
{
    private static string _html = "";

    /// <summary>Gotowy tag &lt;script&gt; (albo pusty string, gdy analityka wyłączona).</summary>
    public static string Html => _html;

    public static void Configure(AnalyticsOptions o)
    {
        if (!o.Enabled) { _html = ""; return; }
        var a = HtmlEncoder.Default;
        _html = $"""<script src="/js/consent.js" defer data-clarity="{a.Encode(o.ClarityProjectId ?? "")}" data-ga="{a.Encode(o.GaMeasurementId ?? "")}"></script>""";
    }

    /// <summary>Rozszerzenia CSP wymagane przez Clarity/GA — dołączane do nagłówka TYLKO gdy
    /// analityka skonfigurowana (bez niej polityka zostaje bajt w bajt jak dotąd).</summary>
    public const string CspScriptSrc = " https://www.clarity.ms https://*.clarity.ms https://www.googletagmanager.com";
    public const string CspConnectSrc = " https://*.clarity.ms https://*.google-analytics.com https://*.analytics.google.com https://www.googletagmanager.com";
    public const string CspImgSrc = " https://*.clarity.ms https://*.google-analytics.com";
}
