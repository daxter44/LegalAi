using PrawoRAG.Domain;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// Logika wchłonięcia nowel do tekstu jednolitego (AKT-1) — czysta, na danych empirycznych z KPC
/// (t.j. DU/2026/468): nowele ogłoszone wcześniej wchłonięte, później — nie.
/// </summary>
public class ConsolidationTests
{
    private const string Tj = "DU/2026/468"; // najnowszy tekst jednolity KPC (ogł. 2026-04-07)

    [Theory]
    [InlineData("DU/2026/473", true)]   // ogłoszona po t.j. → niewchłonięta
    [InlineData("DU/2026/830", true)]
    [InlineData("DU/2025/1172", false)] // z 1.03.2026, ale ogłoszona w 2025 → JEST w kwietniowym t.j.
    [InlineData("DU/2024/1568", false)] // starszy rocznik
    [InlineData("DU/2026/468", false)]  // to sam t.j. — nie „po"
    public void Detects_unabsorbed_amendments(string amendment, bool expected)
        => Assert.Equal(expected, Consolidation.IsUnabsorbed(amendment, Tj));

    [Fact]
    public void Same_year_compares_by_position()
    {
        Assert.True(Consolidation.IsUnabsorbed("DU/2026/469", "DU/2026/468"));
        Assert.False(Consolidation.IsUnabsorbed("DU/2026/467", "DU/2026/468"));
    }

    // --- Warunek VACATIO LEGIS (diagnoza działalności nierejestrowanej, 2026-09-01) ---
    // Dane empiryczne z Prawa przedsiębiorców: nowela DU/2025/1168 (poz. 1168 < t.j. poz. 1480)
    // wypadała z listy po samym kluczu ELI, mimo że wchodzi w życie 2026-01-01 — PO obwieszczeniu
    // t.j. (2025-10-20), więc t.j. drukuje ją jako podwójne brzmienie i NICZEGO nie rozstrzyga.

    [Fact] // regresyjny: dokładnie zmierzony przypadek z diagnozy
    public void Amendment_effective_after_tj_announcement_stays_unabsorbed()
        => Assert.True(Consolidation.IsUnabsorbed(
            "DU/2025/1168", "DU/2025/1480", "2026-01-01", new DateOnly(2025, 10, 20)));

    [Theory]
    [InlineData("2025-08-01", true, false)]  // weszła w życie przed obwieszczeniem t.j. → rozstrzygnięta
    [InlineData("2026-01-01", true, true)]   // vacatio legis → zostaje na liście
    [InlineData(null, true, false)]          // brak daty wejścia → degradacja do reguły klucza
    [InlineData("2026-01-01", false, false)] // brak daty t.j. → degradacja do reguły klucza
    [InlineData("bzdura", true, false)]      // nieparsowalna data → degradacja do reguły klucza
    public void Vacatio_legis_condition(string? effective, bool hasTjDate, bool expected)
        => Assert.Equal(expected, Consolidation.IsUnabsorbed(
            "DU/2025/1168", "DU/2025/1480", effective,
            hasTjDate ? new DateOnly(2025, 10, 20) : null));

    [Fact] // klucz ELI większy wygrywa niezależnie od dat — stare zachowanie nienaruszone
    public void Key_after_tj_is_unabsorbed_regardless_of_dates()
        => Assert.True(Consolidation.IsUnabsorbed("DU/2025/1826", "DU/2025/1480", null, null));

    [Fact] // nieparsowalne adresy → false także w pełnym wariancie (bezpiecznie: nie flagujemy)
    public void Full_condition_requires_parseable_keys()
        => Assert.False(Consolidation.IsUnabsorbed("śmieci", "DU/2025/1480", "2099-01-01", new DateOnly(2025, 10, 20)));

    [Theory]
    [InlineData(null, "DU/2026/468")]
    [InlineData("DU/2026/473", null)]
    [InlineData("śmieci", "DU/2026/468")]
    public void Unparseable_is_safe_false(string? amendment, string? tj)
        => Assert.False(Consolidation.IsUnabsorbed(amendment, tj));

    [Fact]
    public void Key_parses_eli_address()
    {
        Assert.Equal((2026, 468), Consolidation.Key("DU/2026/468"));
        Assert.Null(Consolidation.Key("niepoprawny"));
    }
}
