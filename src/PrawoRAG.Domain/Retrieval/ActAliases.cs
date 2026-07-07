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
    };

    /// <summary>Kanoniczny fragment tytułu dla skrótu (np. „KW" → „Kodeks wykroczeń") albo null, gdy to nie skrót.</summary>
    public static string? Canonical(string? hint) =>
        hint is not null && Map.TryGetValue(hint.Trim(), out var v) ? v : null;
}
