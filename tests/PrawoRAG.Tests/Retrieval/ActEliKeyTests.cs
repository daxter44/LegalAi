using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// T-ACT-REF — odwołanie do aktu po numerze Dziennika Ustaw jako klucz strukturalny (ten sam wzorzec
/// co CaseNumberKeyTests, dla aktów): bezpośrednio ELI ("DU/2025/1815") i naturalny zapis dziennika
/// ("Dz.U. 2025 poz. 1815"), w tym starszy zapis z "Nr" (pre-2012, pozycja jest jedynym trwałym id).
/// </summary>
public class ActEliKeyTests
{
    [Theory]
    [InlineData("DU/2025/1815", "DU/2025/1815")]
    [InlineData("du/1997/553", "DU/1997/553")] // wielkość liter nieistotna
    public void Detect_finds_eli_format(string input, string expected)
        => Assert.Equal([expected], ActEliKey.Detect(input));

    [Theory]
    [InlineData("Dz.U. 2025 poz. 1815", "DU/2025/1815")]
    [InlineData("Dz.U. z 2025 r. poz. 1815", "DU/2025/1815")]
    [InlineData("Dz. U. z 2025 r., poz. 1815", "DU/2025/1815")]
    [InlineData("Dziennik Ustaw z 2025 r. poz. 1815", "DU/2025/1815")]
    [InlineData("DzU 2025 poz 1815", "DU/2025/1815")]
    public void Detect_finds_journal_reference_variants(string input, string expected)
        => Assert.Equal([expected], ActEliKey.Detect(input));

    [Fact] // starszy zapis pre-2012: "Nr NNN" PRZED "poz." — pozycja jest jedynym trwałym identyfikatorem
    public void Detect_ignores_nr_before_poz_pre_2012_style()
    {
        var keys = ActEliKey.Detect("na podstawie Dz.U. z 1964 r. Nr 16, poz. 93 (kodeks cywilny)");
        Assert.Equal(["DU/1964/93"], keys);
    }

    [Fact]
    public void Detect_in_natural_question()
    {
        var keys = ActEliKey.Detect("Co zawiera rozporządzenie Dz.U. 2025 poz. 1815 w sprawie KSeF?");
        Assert.Equal(["DU/2025/1815"], keys);
    }

    [Fact]
    public void Detect_finds_multiple_and_dedups()
    {
        var keys = ActEliKey.Detect("Porównaj DU/2025/1815 z Dz.U. 2025 poz. 1815 oraz DU/1997/553.");
        Assert.Equal(["DU/2025/1815", "DU/1997/553"], keys);
    }

    [Fact]
    public void Detect_no_reference_is_empty()
    {
        Assert.Empty(ActEliKey.Detect("Jakie elementy musi zawierać umowa powierzenia przetwarzania danych?"));
        Assert.Empty(ActEliKey.Detect(""));
    }
}
