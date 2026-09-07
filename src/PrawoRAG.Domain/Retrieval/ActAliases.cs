namespace PrawoRAG.Domain.Retrieval;

/// <summary>
/// Skróty kodeksów → kanoniczna nazwa (fragment tytułu aktu). Szybka ścieżka rozpoznania aktu (QU-2);
/// dla fraz spoza mapy resolver używa dopasowania rozmytego do realnych tytułów w korpusie (pg_trgm).
/// </summary>
public static class ActAliases
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["KK"] = "Kodeks karny",
        ["KPK"] = "Kodeks postępowania karnego",
        ["KKW"] = "Kodeks karny wykonawczy",
        ["KKS"] = "Kodeks karny skarbowy",
        ["KW"] = "Kodeks wykroczeń",
        ["KPW"] = "Kodeks postępowania w sprawach o wykroczenia",
        ["KC"] = "Kodeks cywilny",
        ["KPC"] = "Kodeks postępowania cywilnego",
        ["KP"] = "Kodeks pracy",
        ["KSH"] = "Kodeks spółek handlowych",
        ["KRO"] = "Kodeks rodzinny i opiekuńczy",
        ["KPA"] = "Kodeks postępowania administracyjnego",

        // Prawo UE — nazwy zwyczajowe mapujemy na OZNACZENIE aktu („2016/679"), bo to ono stoi w tytule
        // dokumentu z CELLAR-a („ROZPORZĄDZENIE … (UE) 2016/679 …"). Dzięki temu rozpoznanie idzie tą samą
        // ścieżką (dopasowanie do tytułu w bazie) co kodeksy — bez nowego toru w retrievalu.
        ["RODO"] = "2016/679",
        ["GDPR"] = "2016/679",
        ["AI Act"] = "2024/1689",
        ["AIA"] = "2024/1689",
        ["DSA"] = "2022/2065",
        ["DMA"] = "2022/1925",
        ["DORA"] = "2022/2554",
        ["MiCA"] = "2023/1114",
        ["NIS2"] = "2022/2555",
        ["MDR"] = "2017/745",
        ["REACH"] = "1907/2006",
    };

    /// <summary>Oznaczenie aktu UE („2016/679", „95/46/WE") — jest już kanonicznym fragmentem tytułu,
    /// więc przechodzi bez mapowania.</summary>
    private static readonly System.Text.RegularExpressions.Regex EuDesignator =
        new(@"^\d{1,4}/\d{1,4}(?:/(?:WE|EWG|UE|Euratom|EWWiS))?$",
            System.Text.RegularExpressions.RegexOptions.Compiled
            | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Kanoniczny fragment tytułu dla skrótu („KW" → „Kodeks wykroczeń", „RODO" → „2016/679")
    /// albo null, gdy to ani skrót, ani oznaczenie aktu UE.</summary>
    public static string? Canonical(string? hint)
    {
        if (hint is null) return null;
        var trimmed = hint.Trim();
        if (Map.TryGetValue(trimmed, out var v)) return v;
        return EuDesignator.IsMatch(trimmed) ? trimmed : null;
    }
}
