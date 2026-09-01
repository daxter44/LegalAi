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

    /// <summary>
    /// Pełny warunek „nierozstrzygnięta w t.j." (diagnoza działalności nierejestrowanej, 2026-09-01):
    /// samo „ogłoszona po t.j." przegapia całą klasę VACATIO LEGIS — nowelę ogłoszoną PRZED t.j.,
    /// ale wchodzącą w życie PO jego dacie odcięcia. ISAP drukuje ją wtedy w t.j. jako PODWÓJNE
    /// brzmienie z przypisem („wejdzie w życie…"), więc tekst nie rozstrzyga, a model bez markera
    /// [NOWELIZACJA] dostaje dwie wersje przepisu naraz (zmierzone: DU/2025/1168 poz. 1168 &lt; t.j.
    /// poz. 1480, wejście w życie 2026-01-01 — wypadała z listy i art. 5 Prawa przedsiębiorców
    /// kończył się odmową). Dlatego nowela zostaje na liście też wtedy, gdy jej data wejścia
    /// w życie jest późniejsza niż data OBWIESZCZENIA t.j. Brak którejś daty → zachowanie stare
    /// (sam klucz ELI) — bezpieczna degradacja dla ścieżek bez sieci (ingest w ActNormalizer).
    /// </summary>
    public static bool IsUnabsorbed(
        string? amendmentEli, string? consolidatedTextEli, string? effectiveDate, DateOnly? tjAnnouncedDate)
    {
        if (IsUnabsorbed(amendmentEli, consolidatedTextEli)) return true;
        return Key(amendmentEli) is not null && Key(consolidatedTextEli) is not null
            && tjAnnouncedDate is { } tj
            && DateOnly.TryParse(effectiveDate, out var effective)
            && effective > tj;
    }
}
