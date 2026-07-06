using System.Text;
using System.Text.RegularExpressions;
using PrawoRAG.Domain.Documents;

namespace PrawoRAG.Ingestion.Eli;

/// <summary>
/// Parser aktu z PŁASKIEGO tekstu (ścieżka PDF: teksty jednolite ELI od 2025 są tylko w PDF).
/// Odtwarza tę samą granulację co <see cref="ActNormalizer"/> dla HTML — jeden segment = jedna norma —
/// ale ze strumienia tekstu, nie z DOM. Kroki: (1) usuń bieżące nagłówki stron „Dziennik Ustaw – N – Poz. M";
/// (2) pomiń preambułę obwieszczenia (start od „Załącznik do obwieszczenia", potem pierwszy „Art."/„§") —
/// preambuła cytuje przepisy nowelizujące (np. „Art. 5. Ustawa wchodzi w życie…"), które NIE są treścią aktu;
/// (3) podziel po „Art. N.", w artykule po „§ M."; pomiń jednostki „(uchylony)". Punkty (1) 2) …) zostają w
/// treści segmentu — dzielenie ich z płaskiego tekstu jest zawodne (mylone z odwołaniami); to świadome v1.
/// </summary>
public static class ActTextParser
{
    // Bieżący nagłówek strony wstrzykiwany przez ekstrakcję w środek strumienia. En-dash lub myślnik.
    private static readonly Regex HeaderNoise =
        new(@"Dziennik\s+Ustaw\s*[–—-]\s*\d+\s*[–—-]\s*Poz\.\s*\d+", RegexOptions.Compiled);
    // „Art. 148." / „Art. 43bb." — wielka litera „A" (odwołania „art. …" są małą literą i nie łapią się).
    private static readonly Regex ArtMarker = new(@"Art\.\s*(\d+[a-zA-Z]*)\.", RegexOptions.Compiled);
    // „§ 1." / „§ 1a." — wymóg kropki po numerze odsiewa większość odwołań w treści („w § 3 i 4").
    private static readonly Regex ParaMarker = new(@"§\s*(\d+[a-zA-Z]*)\.", RegexOptions.Compiled);
    private static readonly Regex Ws = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex UchylonyOnly =
        new(@"^\(?\s*uchylon\w+\s*\)?[.\s]*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private const string ZalacznikMarker = "Załącznik do obwieszczenia";

    public static (List<DocumentSegment> Segments, string PlainText) Parse(
        string rawText, string shortTitle, string eliId, string? displayAddress, string? sourceUrl)
    {
        var segments = new List<DocumentSegment>();
        var full = new StringBuilder();
        if (string.IsNullOrWhiteSpace(rawText)) return (segments, "");

        var text = SkipPreamble(Clean(rawText));

        var articles = ArtMarker.Matches(text);
        if (articles.Count == 0)
        {
            // Rozporządzenie: brak artykułów — najwyższym poziomem jest § (jak w ActNormalizer dla HTML).
            EmitParagraphs(text, article: null, shortTitle, eliId, displayAddress, sourceUrl, segments, full);
            return (segments, full.ToString().TrimEnd());
        }

        for (var i = 0; i < articles.Count; i++)
        {
            var article = articles[i].Groups[1].Value;
            var bodyStart = articles[i].Index + articles[i].Length;
            var bodyEnd = i + 1 < articles.Count ? articles[i + 1].Index : text.Length;
            var body = text[bodyStart..bodyEnd].Trim();
            EmitParagraphs(body, article, shortTitle, eliId, displayAddress, sourceUrl, segments, full);
        }

        return (segments, full.ToString().TrimEnd());
    }

    /// <summary>Dzieli treść (artykułu lub całości aktu bezartykułowego) po „§ M."; bez § — całość jednym segmentem.</summary>
    private static void EmitParagraphs(
        string body, string? article, string shortTitle, string eliId, string? displayAddress, string? sourceUrl,
        List<DocumentSegment> segments, StringBuilder full)
    {
        var paras = ParaMarker.Matches(body);
        if (paras.Count == 0)
        {
            Emit(article, paragraph: null, body, shortTitle, eliId, displayAddress, sourceUrl, segments, full);
            return;
        }

        // Wstęp artykułu przed pierwszym § (np. „Art. N. Kto…") — samodzielny segment, gdy niepusty.
        var intro = body[..paras[0].Index].Trim();
        if (intro.Length >= 20)
            Emit(article, paragraph: null, intro, shortTitle, eliId, displayAddress, sourceUrl, segments, full);

        for (var j = 0; j < paras.Count; j++)
        {
            var paragraph = paras[j].Groups[1].Value;
            var s = paras[j].Index + paras[j].Length;
            var e = j + 1 < paras.Count ? paras[j + 1].Index : body.Length;
            var pbody = body[s..e].Trim();
            Emit(article, paragraph, pbody, shortTitle, eliId, displayAddress, sourceUrl, segments, full);
        }
    }

    private static void Emit(
        string? article, string? paragraph, string body, string shortTitle, string eliId,
        string? displayAddress, string? sourceUrl, List<DocumentSegment> segments, StringBuilder full)
    {
        body = body.Trim();
        if (body.Length == 0 || UchylonyOnly.IsMatch(body)) return; // uchylona jednostka — bez pustego chunka

        var artLabel = article is not null
            ? $"Art. {article}" + (paragraph is not null ? $" § {paragraph}" : "")
            : paragraph is not null ? $"§ {paragraph}" : null;
        var header = string.Join(", ", new[] { shortTitle, artLabel }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var text = header.Length > 0 ? header + "\n" + body : body;

        var start = full.Length;
        full.Append(text).Append("\n\n");

        segments.Add(new DocumentSegment
        {
            Text = text,
            Kind = "article",
            Label = artLabel ?? "Art.",
            ContextHeader = header,
            CharStart = start,
            Locator = new CitationLocator
            {
                EliId = eliId,
                Article = article,
                Paragraph = paragraph,
                DisplayAddress = displayAddress,
                SourceUrl = sourceUrl,
            },
        });
    }

    /// <summary>Usuwa nagłówki stron i skleja białe znaki — strumień z PDF ma nagłówki wstrzyknięte w środek treści.</summary>
    private static string Clean(string raw) => Ws.Replace(HeaderNoise.Replace(raw, " "), " ").Trim();

    /// <summary>Odcina preambułę obwieszczenia: od „Załącznik do obwieszczenia" (gdy jest), potem od pierwszego
    /// znacznika „Art."/„§". Chroni przed złapaniem przepisów cytowanych w preambule jako treści aktu.</summary>
    private static string SkipPreamble(string text)
    {
        var zi = text.IndexOf(ZalacznikMarker, StringComparison.Ordinal);
        if (zi > 0) text = text[zi..];

        var art = ArtMarker.Match(text);
        var para = ParaMarker.Match(text);
        var start = (art.Success, para.Success) switch
        {
            (true, true) => Math.Min(art.Index, para.Index),
            (true, false) => art.Index,
            (false, true) => para.Index,
            _ => -1,
        };
        return start > 0 ? text[start..] : text;
    }
}
