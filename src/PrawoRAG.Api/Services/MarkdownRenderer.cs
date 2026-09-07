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

    /// <summary>
    /// Marker cytowania w GOTOWYM HTML: <c>[n]</c> oraz GRUPA <c>[2, 3, 4]</c> — model czasem pisze
    /// grupę zamiast osobnych markerów, a wtedy stary wzorzec (cyfry bezpośrednio przed <c>]</c>)
    /// nie linkował jej wcale i użytkownik widział nieklikalny tekst. Bez limitu do 2 cyfr, bo przy
    /// rozszerzeniu sąsiedztwa aktu (plan SAS) źródeł bywa kilkadziesiąt.
    /// </summary>
    private static readonly Regex CiteRe = new(@"\[\s*\d+(?:\s*,\s*\d+)*\s*\]", RegexOptions.Compiled);

    /// <summary>Marker fragmentu załącznika: [D1], [D2], [D1, D2]… (przestrzeń dokumentu, DOC-5).</summary>
    private static readonly Regex DocCiteRe = new(@"\[D\s*\d+(?:\s*,\s*D?\s*\d+)*\s*\]", RegexOptions.Compiled);

    /// <summary>Numery wyciągane z dopasowanego nawiasu (patrz <see cref="CiteRe"/>).</summary>
    private static readonly Regex NumberRe = new(@"\d+", RegexOptions.Compiled);

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
            html = DocCiteRe.Replace(html, m => LinkGroup(
                m, docCount, n => $"<a class=\"cite\" href=\"#docsrc-{anchorId}-{n}\" " +
                                  $"title=\"Pokaż fragment dokumentu [D{n}]\">[D{n}]</a>"));
        if (sourceCount > 0)
            html = CiteRe.Replace(html, m => LinkGroup(
                m, sourceCount, n => $"<a class=\"cite\" href=\"#src-{anchorId}-{n}\" " +
                                     $"title=\"Pokaż źródło [{n}]\">[{n}]</a>"));
        return html;
    }

    /// <summary>
    /// Zamienia dopasowany nawias na OSOBNY link per numer: <c>[2, 3, 4]</c> → <c>[2] [3] [4]</c>,
    /// rozdzielone spacją (nie przecinkiem — inaczej zostawałby wiszący przecinek POZA linkiem).
    ///
    /// Numery spoza zakresu 1..<paramref name="max"/> zostają tekstem — parytet z dotychczasowym
    /// zachowaniem dla pojedynczych markerów: to kandydaci na fabrykację, łapie ich
    /// <c>CitationValidator</c>, a linkowanie w próżnię tylko udawałoby, że źródło istnieje.
    /// Gdy w grupie NIE MA ani jednego numeru w zakresie, zwracamy oryginał bez zmian.
    /// </summary>
    private static string LinkGroup(Match m, int max, Func<int, string> link)
    {
        var numbers = NumberRe.Matches(m.Value).Select(x => int.Parse(x.Value)).ToList();
        if (numbers.Count == 0) return m.Value;

        var parts = numbers
            .Select(n => n >= 1 && n <= max ? link(n) : $"[{n}]")
            .ToList();

        // Żaden numer nie trafił w zakres → nie przepisujemy markera (mniej niespodzianek w tekście).
        return parts.Any(p => p.StartsWith("<a", StringComparison.Ordinal))
            ? string.Join(" ", parts)
            : m.Value;
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
