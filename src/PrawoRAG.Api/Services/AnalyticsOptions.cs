using System.Text.Encodings.Web;

namespace PrawoRAG.Api.Services;

/// <summary>
/// Analityka bez cookies (US-2.12, decyzja 2026-09-01): self-hostowane Umami zamiast GA4/Clarity —
/// pomiar zagregowany, bez plików cookies, bez identyfikowania osób, na naszej infrastrukturze
/// (kontener w infra/compose.yaml, osobna baza `umami` bez dostępu do bazy z rozmowami).
/// Skrypt nie zapisuje niczego na urządzeniu → poza art. 173 PT: ładowany BEZ banera zgody
/// (warunek prawdziwości Legal/polityka-cookies.md §3 i polityki prywatności pkt 10).
/// Puste wartości (dev/domyślnie) = zero skryptów i zero zmian w CSP.
/// </summary>
public sealed class AnalyticsOptions
{
    public const string SectionName = "Analytics";

    /// <summary>Pełny adres skryptu Umami, np. „https://analytics.przyklad.pl/script.js".
    /// Z jego originu wynika też host dozwolony w CSP (skrypt + beacon /api/send).</summary>
    public string? ScriptUrl { get; set; }

    /// <summary>Identyfikator witryny z panelu Umami (UUID).</summary>
    public string? WebsiteId { get; set; }

    public bool Enabled =>
        !string.IsNullOrWhiteSpace(ScriptUrl) && !string.IsNullOrWhiteSpace(WebsiteId);
}

/// <summary>
/// Wpięcie skryptu Umami do WSZYSTKICH powłok HTML aplikacji (Blazor App.razor, landing, strony
/// kont, /konto, strony prawne). Statyczne — część powłok to statyczne buildery stringów bez DI;
/// konfigurowane RAZ na starcie w Program.cs. Zdarzenia konwersji: atrybut
/// <c>data-umami-event="…"</c> na elemencie (bez inline JS — CSP zostaje czysty).
/// </summary>
public static class AnalyticsSnippet
{
    private static string _html = "";
    private static string _cspOrigin = "";

    /// <summary>Gotowy tag &lt;script&gt; (albo pusty string, gdy analityka wyłączona).</summary>
    public static string Html => _html;

    /// <summary>Origin instancji Umami do CSP (script-src + connect-src) — pusty, gdy wyłączona.</summary>
    public static string CspOrigin => _cspOrigin;

    public static void Configure(AnalyticsOptions o)
    {
        if (!o.Enabled || !Uri.TryCreate(o.ScriptUrl, UriKind.Absolute, out var uri))
        {
            _html = "";
            _cspOrigin = "";
            return;
        }
        var a = HtmlEncoder.Default;
        _html = $"""<script defer src="{a.Encode(o.ScriptUrl!)}" data-website-id="{a.Encode(o.WebsiteId!)}"></script>""";
        _cspOrigin = uri.GetLeftPart(UriPartial.Authority);
    }
}
