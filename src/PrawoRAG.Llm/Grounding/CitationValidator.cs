using System.Text.RegularExpressions;

namespace PrawoRAG.Llm.Grounding;

/// <summary>Wynik kontroli anty-fabrykacji odpowiedzi LLM. <see cref="DocCited"/>/<see cref="DocOutOfRange"/>
/// dotyczą przestrzeni cytowań załącznika [Dk] (DOC-3) — puste, gdy pytanie było bez dokumentu.</summary>
public sealed record CitationCheck(
    IReadOnlyList<int> Cited,
    IReadOnlyList<int> OutOfRange,
    IReadOnlyList<string> SuspiciousReferences,
    IReadOnlyList<int>? DocCited = null,
    IReadOnlyList<int>? DocOutOfRange = null,
    IReadOnlyList<string>? SuspiciousArticles = null,
    IReadOnlyList<string>? SuspiciousCaseNumbers = null)
{
    /// <summary>Czysta = brak cytatów [n]/[Dk] spoza zakresu i brak artykułów/sygnatur nieobecnych w kontekście.</summary>
    public bool IsClean => OutOfRange.Count == 0 && SuspiciousReferences.Count == 0
                           && (DocOutOfRange?.Count ?? 0) == 0;

    /// <summary>
    /// Podejrzane odwołania ROZDZIELONE (Zadanie 9 planu ROU) — bo mają różną precyzję i bramka
    /// z Zadania 10 może chcieć traktować je inaczej. Sygnatura akt ma wąski, wysokoprecyzyjny wzorzec
    /// („II AKo 174/22"), więc jej nieobecność w kontekście to niemal pewna fabrykacja. Numer artykułu
    /// łapie szeroki regex i bywa zaszumiony, więc to sygnał słabszy.
    /// <see cref="SuspiciousReferences"/> zostaje jako SUMA — czyta ją UI i eval, nie ruszamy ich.
    /// </summary>
    public IReadOnlyList<string> Articles => SuspiciousArticles ?? [];

    public IReadOnlyList<string> CaseNumbers => SuspiciousCaseNumbers ?? [];
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

        var suspiciousArticles = new List<string>();
        foreach (Match m in ArticleRegex().Matches(answer))
            if (!ArticlePresent(haystack, m.Value))
                suspiciousArticles.Add(m.Value);

        var suspiciousCases = new List<string>();
        foreach (Match m in CaseNumberRegex().Matches(answer))
            if (!ContainsNormalized(haystack, m.Value))
                suspiciousCases.Add(m.Value);

        suspiciousArticles = suspiciousArticles.Distinct().ToList();
        suspiciousCases = suspiciousCases.Distinct().ToList();

        return new CitationCheck(
            cited, outOfRange,
            [.. suspiciousArticles, .. suspiciousCases],   // suma — kompatybilność wstecz (UI, eval)
            docCited, docOutOfRange,
            suspiciousArticles, suspiciousCases);
    }

    /// <summary>
    /// Czy odwołanie do artykułu ma pokrycie w kontekście. Dwa etapy, bo dosłowne porównanie
    /// produkowało FAŁSZYWE ALARMY na wariantach zapisu, które w aktach są normą: model pisze
    /// „art. 5 ust. 1", a tekst jednolity ma „Art. 5. 1." — ta sama jednostka, inny zapis.
    ///
    /// To nie jest kosmetyka: od Zadania 10 ten sygnał ZAWRACA odpowiedź do regeneracji albo ją
    /// blokuje, więc fałszywy alarm zamienia poprawną odpowiedź na odmowę. Próg zabicia bramki
    /// w planie to >10% takich przypadków.
    ///
    /// Etap 2 obcina WYŁĄCZNIE sufiksy jednostek niższego rzędu (<c>ust./§/pkt/lit.</c>) i wymaga,
    /// żeby RDZEŃ („art. 5", „art. 1a") był obecny — z granicą słowa, więc „art. 1" nie zaliczy się
    /// jako pokrycie dla „art. 1a" (to inny przepis) ani „art. 5" dla „art. 55".
    /// </summary>
    private static bool ArticlePresent(string haystack, string reference)
    {
        if (ContainsNormalized(haystack, reference)) return true;

        var core = ArticleCoreRegex().Match(reference);
        if (!core.Success) return false;

        var number = core.Groups[1].Value;
        return ArticleInContext(haystack, number);
    }

    /// <summary>Czy w kontekście stoi artykuł o TYM numerze — z granicą po numerze, żeby nie mieszać
    /// „art. 1" z „art. 1a" ani „art. 5" z „art. 55".</summary>
    private static bool ArticleInContext(string haystack, string number) =>
        Regex.IsMatch(
            WhitespaceRegex().Replace(haystack, " "),
            @"\bart\.?\s*" + Regex.Escape(number) + @"(?![\p{L}\d])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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

    // Rdzeń odwołania: sam numer artykułu (z ewentualną literą — „1a", „43bb"), BEZ jednostek
    // niższego rzędu. Litera jest częścią rdzenia, bo art. 1a to inny przepis niż art. 1.
    [GeneratedRegex(@"art\.?\s*(\d+[a-z]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArticleCoreRegex();

    // np. „II AKo 174/22", „I ACa 772/13"
    [GeneratedRegex(@"\b[IVXLC]{1,4}\s+[A-Za-zŁłŚśŻżĄąĘę]{1,5}\s+\d+/\d{2,4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex CaseNumberRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
