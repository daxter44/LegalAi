using System.Text.RegularExpressions;

namespace PrawoRAG.Domain.Retrieval;

/// <summary>Odwołanie do konkretnej jednostki aktu wyłuskane z pytania. <see cref="ActHint"/> to surowa
/// wskazówka aktu (fraza „kodeks…" albo skrót „KW") — rozpoznaniem na dokument zajmuje się osobny resolver.</summary>
public sealed record CitationRef(string Article, string? Paragraph, string? ActHint);

/// <summary>
/// Deterministyczny ekstraktor cytatów z pytania (QU-0, P1/P2/P4). Bez zależności, w pełni testowalny.
/// Numer artykułu ma małą wariancję → regex tolerancyjny (skróty, brak kropek). Nazwa aktu (skróty +
/// polska odmiana) → fraza „kodeks…" albo skrót; ostateczne rozpoznanie robi resolver (aliasy + pg_trgm).
/// </summary>
public static class CitationParser
{
    // „art.", „art", „artykuł/artykule/artykułu…" + numer (może mieć literę: „43bb", „175da").
    private static readonly Regex ArtRe =
        new(@"\bart(?:yku\w*|\.)?\s*(\d+[a-zA-Z]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Kolejne numery w wyliczeniu „art. 94 i 95", „art. 5, 6 oraz 7".
    private static readonly Regex ChainRe =
        new(@"\G\s*(?:,|i|oraz)\s*(\d+[a-zA-Z]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ParaRe = new(@"§\s*(\d+[a-zA-Z]*)", RegexOptions.Compiled);
    // Fraza aktu: „Kodeksu wykroczeń", „kodeks postępowania cywilnego" (kodeks + 1-2 słowa).
    private static readonly Regex KodeksRe =
        new(@"kodeks\w*(?:\s+\p{L}+){1,2}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Ws = new(@"\s+", RegexOptions.Compiled);

    // Skróty kodeksów — litery mogą być rozdzielone kropką/spacją („k.p.c.", „KPC", „k. w."). Dłuższe pierwsze.
    private static readonly (string Norm, Regex Re)[] Abbrevs =
        new[] { "KPC", "KPK", "KKW", "KKS", "KRO", "KPA", "KSH", "KK", "KC", "KW", "KP" }
        .Select(a => (a, new Regex(@"(?<![\p{L}.])" + string.Join(@"\.?\s?", a.Select(ch => Regex.Escape(ch.ToString())))
            + @"\.?(?![\p{L}])", RegexOptions.Compiled | RegexOptions.IgnoreCase)))
        .ToArray();

    public static IReadOnlyList<CitationRef> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var articles = new List<(string No, int End)>();
        foreach (Match m in ArtRe.Matches(text))
        {
            articles.Add((m.Groups[1].Value, m.Index + m.Length));
            // Wyliczenie po numerze: „…94 i 95 oraz 96".
            var pos = m.Index + m.Length;
            for (var chain = ChainRe.Match(text, pos); chain.Success; chain = ChainRe.Match(text, chain.Index + chain.Length))
                articles.Add((chain.Groups[1].Value, chain.Index + chain.Length));
        }
        if (articles.Count == 0) return [];

        var actHint = ActHint(text);
        var refs = new List<CitationRef>(articles.Count);
        for (var i = 0; i < articles.Count; i++)
        {
            // Paragraf informacyjnie: pierwszy § po pierwszym artykule (i tak pobieramy cały artykuł — P3).
            string? para = i == 0 && ParaRe.Match(text, articles[0].End) is { Success: true } p ? p.Groups[1].Value : null;
            refs.Add(new CitationRef(articles[i].No, para, actHint));
        }
        return refs;
    }

    private static string? ActHint(string text)
    {
        var k = KodeksRe.Match(text);
        if (k.Success) return Ws.Replace(k.Value, " ").Trim();
        foreach (var (norm, re) in Abbrevs)
            if (re.IsMatch(text)) return norm;
        return null;
    }
}
