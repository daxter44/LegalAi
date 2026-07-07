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

    public static string ToSafeHtml(string? markdown)
        => string.IsNullOrEmpty(markdown) ? "" : Sanitizer.Sanitize(Markdown.ToHtml(markdown, Pipeline));

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
