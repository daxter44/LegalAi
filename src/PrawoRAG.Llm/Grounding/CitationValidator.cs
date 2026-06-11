using System.Text.RegularExpressions;

namespace PrawoRAG.Llm.Grounding;

/// <summary>Wynik kontroli anty-fabrykacji odpowiedzi LLM.</summary>
public sealed record CitationCheck(
    IReadOnlyList<int> Cited,
    IReadOnlyList<int> OutOfRange,
    IReadOnlyList<string> SuspiciousReferences)
{
    /// <summary>Czysta = brak cytatów [n] spoza zakresu i brak artykułów/sygnatur nieobecnych w kontekście.</summary>
    public bool IsClean => OutOfRange.Count == 0 && SuspiciousReferences.Count == 0;
}

/// <summary>
/// Anty-fabrykacja: sprawdza, że odpowiedź odwołuje się tylko do dostarczonych źródeł [1..K]
/// oraz że przywołane artykuły/sygnatury faktycznie występują w kontekście (a nie zostały zmyślone).
/// </summary>
public static partial class CitationValidator
{
    public static CitationCheck Validate(string answer, IReadOnlyList<string> contextTexts, int sourceCount)
    {
        var cited = MarkerRegex().Matches(answer)
            .Select(m => int.Parse(m.Groups[1].Value))
            .Distinct().OrderBy(x => x).ToList();

        var outOfRange = cited.Where(n => n < 1 || n > sourceCount).ToList();

        var haystack = string.Join("\n", contextTexts);
        var suspicious = new List<string>();

        foreach (Match m in ArticleRegex().Matches(answer))
            if (!ContainsNormalized(haystack, m.Value))
                suspicious.Add(m.Value);

        foreach (Match m in CaseNumberRegex().Matches(answer))
            if (!ContainsNormalized(haystack, m.Value))
                suspicious.Add(m.Value);

        return new CitationCheck(cited, outOfRange, suspicious.Distinct().ToList());
    }

    private static bool ContainsNormalized(string haystack, string needle)
    {
        static string N(string s) => WhitespaceRegex().Replace(s, " ").Trim().ToLowerInvariant();
        return N(haystack).Contains(N(needle));
    }

    [GeneratedRegex(@"\[(\d+)\]")]
    private static partial Regex MarkerRegex();

    [GeneratedRegex(@"art\.?\s*\d+[a-z]?(\s*§\s*\d+)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArticleRegex();

    // np. „II AKo 174/22", „I ACa 772/13"
    [GeneratedRegex(@"\b[IVXLC]{1,4}\s+[A-Za-zŁłŚśŻżĄąĘę]{1,5}\s+\d+/\d{2,4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex CaseNumberRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
