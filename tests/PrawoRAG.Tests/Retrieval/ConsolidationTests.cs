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
