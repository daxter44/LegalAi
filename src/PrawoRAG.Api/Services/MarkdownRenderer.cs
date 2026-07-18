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

    public static string ToSafeHtml(string? markdown)
        => string.IsNullOrEmpty(markdown) ? "" : Sanitizer.Sanitize(Markdown.ToHtml(markdown, Pipeline));

    /// <summary>
    /// Wariant z klikalnymi cytowaniami: markery [n] w ZAKRESIE 1..<paramref name="sourceCount"/>
    /// stają się kotwicami do kart źródeł (<c>#src-{anchorId}-{n}</c>). Podmiana po sanityzacji —
    /// kotwica jest bezpieczna z konstrukcji (n = cyfry z regexa, anchorId generujemy sami), a sam
    /// sanitizer nie musi przepuszczać linków fragmentowych. Markery spoza zakresu zostają tekstem
    /// (to kandydaci na fabrykację — łapie je CitationValidator, nie linkujemy w próżnię).
    /// </summary>
    public static string ToSafeHtml(string? markdown, int sourceCount, string anchorId)
    {
        var html = ToSafeHtml(markdown);
        if (html.Length == 0 || sourceCount <= 0) return html;
        return CiteRe.Replace(html, m =>
            int.Parse(m.Groups[1].Value) is var n && n >= 1 && n <= sourceCount
                ? $"<a class=\"cite\" href=\"#src-{anchorId}-{n}\" title=\"Pokaż źródło [{n}]\">[{n}]</a>"
                : m.Value);
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
