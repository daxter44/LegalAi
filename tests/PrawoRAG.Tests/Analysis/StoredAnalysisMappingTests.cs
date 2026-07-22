using PrawoRAG.Api.Services;
using PrawoRAG.Llm.Grounding;

namespace PrawoRAG.Tests.Analysis;

/// <summary>
/// T-AN-5 — snapshot zdegradowany z rekordu DB (czysta funkcja): puste Text, dziury po
/// nieprzeanalizowanych jednostkach wypełnione placeholderami (UI indeksuje Results[Index-1]),
/// Total mówi prawdę (X z N). Plus: kotwica dopytań bez segmentu „Treść:" przy pustym Text.
/// </summary>
public class StoredAnalysisMappingTests
{
    private static StoredUnit Unit(int index, UnitVerdict verdict = UnitVerdict.Ok) =>
        new(Guid.CreateVersion7(), index, $"§ {index}", verdict, $"Uzasadnienie {index} [1].",
            [new ChatSource(1, "art. 484 KC", "KC", null, "…")], true, null, null);

    private static StoredAnalysis Stored(int total, params StoredUnit[] units) =>
        new(Guid.CreateVersion7(), "umowa.pdf", 5, "oceń ryzyka", AnalysisStatus.Interrupted,
            total, false, "Streszczenie.", null, units);

    [Fact]
    public void Maps_units_with_empty_text_and_gap_placeholders()
    {
        // Przerwana analiza: ukończone 1 i 3, jednostka 2 nigdy nie dostała wyniku.
        var snap = Stored(total: 4, Unit(1), Unit(3, UnitVerdict.Risk)).ToSnapshot();

        Assert.Equal(4, snap.Total);                        // prawda o rozmiarze dokumentu
        Assert.Equal(2, snap.Completed);
        Assert.All(snap.Units, u => Assert.Equal("", u.Text));   // treść NIE jest przechowywana
        Assert.Equal(["§ 1", "fragment 2", "§ 3", "fragment 4"], snap.Units.Select(u => u.Heading));
        Assert.Null(snap.Results[1]);                        // dziura = null (UI: „nieprzeanalizowany")
        Assert.Equal(UnitVerdict.Risk, snap.Results[2]!.Verdict); // pozycja = Index-1 zachowana
        Assert.Equal("art. 484 KC", Assert.Single(snap.Results[0]!.Sources).Label);
    }

    [Fact]
    public void ComposeAnchorTurn_omits_text_segment_for_degraded_snapshot()
    {
        var snap = Stored(total: 2, Unit(1), Unit(2)).ToSnapshot();

        var turn = AnalysisFollowUp.ComposeAnchorTurn(snap, [1]);

        Assert.DoesNotContain("Treść:", turn.Answer);            // pusta etykieta myliłaby model
        Assert.Contains("§ 1 — OK", turn.Answer);                // werdykty i uzasadnienia zostają
        Assert.Contains("Uzasadnienie 1", turn.Answer);
        Assert.True(turn.Answer!.Length <= GroundedPrompt.MaxHistoryAnswerChars + 1);
    }
}
