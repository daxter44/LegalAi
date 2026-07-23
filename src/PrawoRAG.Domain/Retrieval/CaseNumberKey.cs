using System.Text.RegularExpressions;

namespace PrawoRAG.Domain.Retrieval;

/// <summary>
/// Sygnatura akt jako KLUCZ STRUKTURALNY (nie tekst do wyszukiwania semantycznego). Prawnik podaje
/// sygnaturę oczekując DOKŁADNIE tego orzeczenia — to identyfikator, nie zapytanie o podobieństwo.
/// Dwie funkcje: <see cref="Normalize"/> (kanoniczny klucz exact-match: trim + pojedyncze spacje +
/// wielkie litery) i <see cref="Detect"/> (wyłuskanie sygnatur z pytania w języku naturalnym).
///
/// Wzorzec obejmuje OBA kształty polskich sygnatur:
///   • bez „/" w środku: „II FSK 1938/08", „II AKa 137/16", „I OSK 1/20";
///   • z kodem miejsca po „/": „III SA/Po 154/26", „I SA/Wr 183/08" (sądy administracyjne WSA).
/// Świadomie SZERSZY niż <c>CitationValidator.CaseNumberRegex</c>, który gubi wariant z „/" (WSA).
/// </summary>
public static partial class CaseNumberKey
{
    // IgnoreCase: prawnik wpisuje sygnaturę różną wielkością liter („ix zz 1/20", „III SA/Po…") —
    // dopasowanie musi być niewrażliwe, a Normalize i tak sprowadza do wielkich liter.
    [GeneratedRegex(
        @"\b[IVXLC]{1,4}\s+[A-Za-zŁłŚśŻżĄąĆćĘęŃńÓóŹź]{1,4}(?:/[A-Za-zŁłŚśŻżĄąĆćĘęŃńÓóŹź]{1,3})?\s+\d+/\d{2,4}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SignatureRegex();

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Kanoniczny klucz do porównania exact-match — MUSI być identyczny po stronie zapisu
    /// (kolumna) i zapytania. Null/puste → null (brak sygnatury do indeksowania).</summary>
    public static string? Normalize(string? caseNumber)
    {
        if (string.IsNullOrWhiteSpace(caseNumber)) return null;
        var collapsed = Whitespace.Replace(caseNumber.Trim(), " ");
        return collapsed.Length == 0 ? null : collapsed.ToUpperInvariant();
    }

    /// <summary>Sygnatury wykryte w tekście pytania, znormalizowane i odduplikowane (kolejność wystąpień).</summary>
    public static IReadOnlyList<string> Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (Match m in SignatureRegex().Matches(text))
            if (Normalize(m.Value) is { } key && seen.Add(key))
                result.Add(key);
        return result;
    }
}
