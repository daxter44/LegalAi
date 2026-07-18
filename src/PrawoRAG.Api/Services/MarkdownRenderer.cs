using System.Text.RegularExpressions;
using Ganss.Xss;
using Markdig;

namespace PrawoRAG.Api.Services;

/// <summary>
/// Renderuje odpowiedź LLM jako BEZPIECZNY HTML (C3/FE-3.4): Markdig z wyłączonym surowym HTML
/// (`DisableHtml`) + sanityzacja allowlistą (HtmlSanitizer) z ograniczeniem schematów linków do
/// http/https/mailto. Broni przed XSS przez wstrzyknięty HTML/`<script>`/`javascript:`-linki.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().DisableHtml().UseAutoLinks().Build();

    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    /// <summary>Marker cytowania w GOTOWYM HTML: [n], 1-2 cyfry (TopK ≤ kilkanaście źródeł).</summary>
    private static readonly Regex CiteRe = new(@"\[(\d{1,2})\]", RegexOptions.Compiled);

    /// <summary>Marker fragmentu załącznika: [D1], [D2]… (przestrzeń dokumentu, DOC-5).</summary>
    private static readonly Regex DocCiteRe = new(@"\[D(\d{1,2})\]", RegexOptions.Compiled);

    public static string ToSafeHtml(string? markdown)
        => string.IsNullOrEmpty(markdown) ? "" : Sanitizer.Sanitize(Markdown.ToHtml(markdown, Pipeline));

    /// <summary>
    /// Wariant z klikalnymi cytowaniami: markery [n] w ZAKRESIE 1..<paramref name="sourceCount"/>
    /// stają się kotwicami do kart źródeł (<c>#src-{anchorId}-{n}</c>), a [Dk] w zakresie
    /// 1..<paramref name="docCount"/> — do kart fragmentów załącznika (<c>#docsrc-{anchorId}-{k}</c>).
    /// Podmiana po sanityzacji — kotwica jest bezpieczna z konstrukcji (n = cyfry z regexa, anchorId
    /// generujemy sami), a sam sanitizer nie musi przepuszczać linków fragmentowych. Markery spoza
    /// zakresu zostają tekstem (to kandydaci na fabrykację — łapie je CitationValidator, nie
    /// linkujemy w próżnię).
    /// </summary>
    public static string ToSafeHtml(string? markdown, int sourceCount, string anchorId, int docCount = 0)
    {
        var html = ToSafeHtml(markdown);
        if (html.Length == 0) return html;
        if (docCount > 0)
            html = DocCiteRe.Replace(html, m =>
                int.Parse(m.Groups[1].Value) is var k && k >= 1 && k <= docCount
                    ? $"<a class=\"cite\" href=\"#docsrc-{anchorId}-{k}\" title=\"Pokaż fragment dokumentu [D{k}]\">[D{k}]</a>"
                    : m.Value);
        if (sourceCount > 0)
            html = CiteRe.Replace(html, m =>
                int.Parse(m.Groups[1].Value) is var n && n >= 1 && n <= sourceCount
                    ? $"<a class=\"cite\" href=\"#src-{anchorId}-{n}\" title=\"Pokaż źródło [{n}]\">[{n}]</a>"
                    : m.Value);
        return html;
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        var s = new HtmlSanitizer();
        s.AllowedSchemes.Clear();
        s.AllowedSchemes.Add("https");
        s.AllowedSchemes.Add("http");
        s.AllowedSchemes.Add("mailto");
        return s;
    }
}
