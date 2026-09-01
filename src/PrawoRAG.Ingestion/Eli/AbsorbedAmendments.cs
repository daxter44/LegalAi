namespace PrawoRAG.Ingestion.Eli;

/// <summary>
/// Klasyfikacja „wchłoniętych nowelizacji" (analiza ANALIZA-NADGODZINY-WCHLONIETE-NOWELE-POMIAR-2026-09-01):
/// ustawa nowelizująca, której zmiany żyją już w tekście jednolitym aktu bazowego, nie powinna konkurować
/// w torach semantycznych retrievalu z aktualnym przepisem — jej krótkie, „czyste" chunki wygrywają
/// przestarzałą treścią (zmierzone: 18–62% top-50 dla typowych pytań).
///
/// Dwa sygnały składają się na flagę <c>documents.AbsorbedAmendment</c>:
/// 1. tytuł nowelizacyjny („o zmianie ustawy…", „o zmianie niektórych ustaw…") — heurystyka ŚWIADOMIE
///    konserwatywna: ~50 aktów „o zmianie nazw/zakresu obowiązywania/Konstytucji” celowo zostaje poza
///    flagą (wolimy zostawić nowelę w retrievalu niż wyciąć akt merytoryczny; status ISAP w naszych
///    metadanych nie rozróżnia wchłonięcia, a „Akty zmienione” z ELI nie są zapisywane);
/// 2. nieobecność ELI aktu na JAKIEJKOLWIEK liście <c>unabsorbedAmendments</c> aktów bazowych —
///    świeże, niewchłonięte nowele (strażnik świeżości AKT) nigdy nie dostają flagi.
///
/// Flaga liczona jest zbiorczo jednym UPDATE (obie strony: nowela wchłonięta po nowym t.j. → true,
/// ponowne pojawienie się na liście → false) — po relinku AKT-5.2 oraz w trybie backfillu
/// <c>Ingestion__Mode=absorbed-flags</c>. Bez re-embeddingu.
/// </summary>
public static class AbsorbedAmendments
{
    /// <summary>Wzorce tytułu nowelizacyjnego — MUSZĄ być zgodne z ILIKE w <see cref="RecomputeSql"/>.</summary>
    public static readonly string[] TitlePatterns = ["o zmianie ustaw", "o zmianie niektórych ustaw"];

    /// <summary>Czy tytuł wskazuje ustawę nowelizującą (odpowiednik warunku ILIKE z RecomputeSql).</summary>
    public static bool IsAmendmentTitle(string? title) =>
        title is not null
        && TitlePatterns.Any(p => title.Contains(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Zbiorcze przeliczenie flagi dla WSZYSTKICH aktów ELI. Zwraca liczbę zmienionych wierszy.
    /// IS DISTINCT FROM — pisze tylko realne zmiany (idempotentny, tani w stanie ustalonym).
    /// </summary>
    public const string RecomputeSql = """
        WITH unabs AS (
            SELECT DISTINCT jsonb_array_elements("TypedMetadata"->'unabsorbedAmendments')->>'EliId' AS eli
            FROM documents
            WHERE "DocType" = 'act' AND "TypedMetadata" ? 'unabsorbedAmendments'
        ), expected AS (
            SELECT d."Id",
                   (d."Source" = 'ELI'
                    AND (d."Title" ILIKE '%o zmianie ustaw%' OR d."Title" ILIKE '%o zmianie niektórych ustaw%')
                    AND NOT EXISTS (SELECT 1 FROM unabs u WHERE u.eli = d."ExternalId")) AS v
            FROM documents d
            WHERE d."DocType" = 'act'
        )
        UPDATE documents d
        SET "AbsorbedAmendment" = e.v, "UpdatedAt" = now()
        FROM expected e
        WHERE d."Id" = e."Id" AND d."AbsorbedAmendment" IS DISTINCT FROM e.v
        """;
}
