namespace PrawoRAG.Domain;

/// <summary>Odwołanie do nowelizacji niewchłoniętej jeszcze do tekstu jednolitego (do dołączenia jako źródło).</summary>
public sealed record AmendmentRef(string EliId, string? EffectiveDate);

/// <summary>
/// Logika aktualności prawa (AKT-1). Tekst jednolity wchłania nowele OGŁOSZONE przed jego datą odcięcia;
/// nowela ogłoszona później obowiązuje, ale nie ma jej w żadnym t.j. Proxy „po ogłoszeniu" = porównanie
/// kluczy ELI (rok, pozycja Dz.U.) — deterministyczne, bez dodatkowych zapytań. Czyste, testowalne.
/// </summary>
public static class Consolidation
{
    /// <summary>„DU/2026/468" → (2026, 468); null, gdy adresu nie da się sparsować.</summary>
    public static (int Year, int Pos)? Key(string? eli)
    {
        if (string.IsNullOrWhiteSpace(eli)) return null;
        var parts = eli.Split('/');
        return parts.Length >= 3 && int.TryParse(parts[^2], out var y) && int.TryParse(parts[^1], out var p)
            ? (y, p) : null;
    }

    /// <summary>
    /// True, gdy nowela została ogłoszona PO tekście jednolitym (klucz ELI większy) — czyli NIE jest w nim
    /// wchłonięta. Gdy któregokolwiek adresu nie da się sparsować → false (bezpiecznie: nie flagujemy).
    /// </summary>
    public static bool IsUnabsorbed(string? amendmentEli, string? consolidatedTextEli)
    {
        var a = Key(amendmentEli);
        var t = Key(consolidatedTextEli);
        return a is not null && t is not null && a.Value.CompareTo(t.Value) > 0;
    }
}
