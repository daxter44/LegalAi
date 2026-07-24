using System.Text.RegularExpressions;

namespace PrawoRAG.Domain.Retrieval;

/// <summary>
/// Odwołanie do KONKRETNEGO AKTU po numerze Dziennika Ustaw jako KLUCZ STRUKTURALNY — ten sam wzorzec
/// co <see cref="CaseNumberKey"/>, ale dla aktów zamiast orzeczeń: „Dz.U. 2025 poz. 1815" albo
/// bezpośrednio ELI „DU/2025/1815" to identyfikator DOKUMENTU, nie zapytanie o podobieństwo.
/// Kanoniczna forma („DU/{rok}/{pozycja}") jest już naturalnym kluczem ingestii ELI
/// (<c>documents.ExternalId</c>) — bez backfillu, bez nowej kolumny.
///
/// Format „Dz.U." toleruje: „z"/„r." opcjonalne, starszy zapis z „Nr NNN" przed „poz." (pre-2012;
/// pozycja jest jedynym trwałym identyfikatorem — „Nr" ignorujemy, tak jak ELI Sejmu).
/// </summary>
public static partial class ActEliKey
{
    // „DU/2025/1815" — bezpośrednio w formacie ELI.
    [GeneratedRegex(@"\bDU/(?<year>\d{4})/(?<pos>\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EliRegex();

    // „Dz.U. [z] 2025 [r.] [Nr NNN,] poz. 1815" / „Dziennik Ustaw…" — starszy zapis miewa „Nr" PRZED „poz."
    // (pozycja jest jedynym trwałym identyfikatorem od 2012, ELI go używa też wstecznie dla starszych aktów).
    [GeneratedRegex(
        @"\b(?:Dz\.?\s*U\.?|Dziennik\s+Ustaw)\s*(?:z\s+)?(?<year>\d{4})\s*r?\.?,?\s*(?:Nr\.?\s*\d+\s*,?\s*)?poz\.?\s*(?<pos>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JournalRegex();

    /// <summary>Kanoniczny klucz do porównania exact-match z <c>documents.ExternalId</c>.</summary>
    private static string Normalize(string year, string pos) => $"DU/{year}/{int.Parse(pos)}";

    /// <summary>Odwołania do aktów wykryte w tekście pytania, znormalizowane do <c>ExternalId</c>
    /// i odduplikowane (kolejność wystąpień).</summary>
    public static IReadOnlyList<string> Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var seen = new HashSet<string>();
        var result = new List<string>();

        void Add(Match m)
        {
            var key = Normalize(m.Groups["year"].Value, m.Groups["pos"].Value);
            if (seen.Add(key)) result.Add(key);
        }

        foreach (Match m in EliRegex().Matches(text)) Add(m);
        foreach (Match m in JournalRegex().Matches(text)) Add(m);
        return result;
    }
}
