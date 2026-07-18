using System.Text.RegularExpressions;

namespace PrawoRAG.Llm.Grounding;

/// <summary>Wynik kontroli anty-fabrykacji odpowiedzi LLM. <see cref="DocCited"/>/<see cref="DocOutOfRange"/>
/// dotyczą przestrzeni cytowań załącznika [Dk] (DOC-3) — puste, gdy pytanie było bez dokumentu.</summary>
public sealed record CitationCheck(
    IReadOnlyList<int> Cited,
    IReadOnlyList<int> OutOfRange,
    IReadOnlyList<string> SuspiciousReferences,
    IReadOnlyList<int>? DocCited = null,
    IReadOnlyList<int>? DocOutOfRange = null)
{
    /// <summary>Czysta = brak cytatów [n]/[Dk] spoza zakresu i brak artykułów/sygnatur nieobecnych w kontekście.</summary>
    public bool IsClean => OutOfRange.Count == 0 && SuspiciousReferences.Count == 0
                           && (DocOutOfRange?.Count ?? 0) == 0;
}

/// <summary>
/// Anty-fabrykacja: sprawdza, że odpowiedź odwołuje się tylko do dostarczonych źródeł [1..K]
/// (oraz fragmentów załącznika [D1..M], gdy pytanie miało dokument) i że przywołane
/// artykuły/sygnatury faktycznie występują w kontekście (a nie zostały zmyślone).
/// </summary>
public static partial class CitationValidator
{
    public static CitationCheck Validate(string answer, IReadOnlyList<string> contextTexts, int sourceCount)
        => Validate(answer, contextTexts, sourceCount, [], 0);

    /// <summary>Wariant z załącznikiem (DOC-3): markery [Dk] walidowane przeciw liczbie fragmentów;
    /// teksty fragmentów wchodzą do stogu — cytat z dokumentu („art. 5 umowy") nie może być
    /// fałszywie oflagowany jako zmyślony artykuł.</summary>
    public static CitationCheck Validate(
        string answer, IReadOnlyList<string> contextTexts, int sourceCount,
        IReadOnlyList<string> docTexts, int docFragmentCount)
    {
        var cited = MarkerRegex().Matches(answer)
            .Select(m => int.Parse(m.Groups[1].Value))
            .Distinct().OrderBy(x => x).ToList();

        var outOfRange = cited.Where(n => n < 1 || n > sourceCount).ToList();

        var docCited = DocMarkerRegex().Matches(answer)
            .Select(m => int.Parse(m.Groups[1].Value))
            .Distinct().OrderBy(x => x).ToList();

        var docOutOfRange = docCited.Where(n => n < 1 || n > docFragmentCount).ToList();

        var haystack = string.Join("\n", contextTexts.Concat(docTexts));
        var suspicious = new List<string>();

        foreach (Match m in ArticleRegex().Matches(answer))
            if (!ContainsNormalized(haystack, m.Value))
                suspicious.Add(m.Value);

        foreach (Match m in CaseNumberRegex().Matches(answer))
            if (!ContainsNormalized(haystack, m.Value))
                suspicious.Add(m.Value);

        return new CitationCheck(cited, outOfRange, suspicious.Distinct().ToList(), docCited, docOutOfRange);
    }

    private static bool ContainsNormalized(string haystack, string needle)
    {
        static string N(string s) => WhitespaceRegex().Replace(s, " ").Trim().ToLowerInvariant();
        return N(haystack).Contains(N(needle));
    }

    [GeneratedRegex(@"\[(\d+)\]")]
    private static partial Regex MarkerRegex();

    // Przestrzeń załącznika: [D1], [D2]… — rozłączna z [n] (MarkerRegex nie łapie litery D).
    [GeneratedRegex(@"\[D(\d+)\]")]
    private static partial Regex DocMarkerRegex();

    [GeneratedRegex(@"art\.?\s*\d+[a-z]?(\s*§\s*\d+)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArticleRegex();

    // np. „II AKo 174/22", „I ACa 772/13"
    [GeneratedRegex(@"\b[IVXLC]{1,4}\s+[A-Za-zŁłŚśŻżĄąĘę]{1,5}\s+\d+/\d{2,4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex CaseNumberRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
