using PrawoRAG.Api.Services;
using PrawoRAG.Llm.Grounding;

namespace PrawoRAG.Tests.Analysis;

/// <summary>
/// T-SPK-6 — dopytania po raporcie: routing mechaniczny po odwołaniach (§/art./pkt, także części
/// „(cz. n)"), fallback embeddingowy w kolejności dokumentu, kompozycja tury-kotwicy (wybrane
/// jednostki z przodu, budżet 1500 znaków, tabela werdyktów dla pytań przekrojowych, kotwice źródeł).
/// </summary>
public class AnalysisFollowUpTests
{
    private static IReadOnlyList<DocUnit> Units(params string[] headings) =>
        headings.Select((h, i) => new DocUnit(i + 1, h, $"{h} Treść postanowienia o karach i czynszu.")).ToList();

    private static AnalysisSnapshot Snap(
        IReadOnlyList<DocUnit> units, string? summary = "Umowa ma jedno ryzyko w § 2.",
        Func<DocUnit, UnitAnalysis?>? result = null)
    {
        result ??= u => new UnitAnalysis(u.Index, u.Heading,
            u.Index == 2 ? UnitVerdict.Risk : UnitVerdict.Ok,
            $"Uzasadnienie dla {u.Heading} [1].",
            [new ChatSource(1, $"art. {u.Index} KC", "Kodeks cywilny", null, "…")]);
        return new AnalysisSnapshot(Guid.NewGuid(), "umowa.pdf", 3, "oceń ryzyka",
            AnalysisStatus.Done, units, false, units.Select(result).ToList(),
            units.Count, summary, null);
    }

    [Theory]
    [InlineData("a co dokładnie z § 2?", new[] { 2 })]
    [InlineData("czy paragraf 3 jest ważny?", new[] { 3 })]
    [InlineData("porównaj § 1 i § 3", new[] { 1, 3 })]
    [InlineData("które paragrafy są ryzykowne?", new int[0])] // brak konkretnego odwołania
    public void FindReferencedUnits_routes_by_explicit_reference(string question, int[] expected)
    {
        var units = Units("§ 1", "§ 2", "§ 3");
        Assert.Equal(expected, AnalysisFollowUp.FindReferencedUnits(question, units));
    }

    [Fact] // odwołanie „§ 2" łapie wszystkie części pociętej jednostki
    public void Reference_matches_all_parts_of_split_unit()
    {
        var units = Units("§ 1", "§ 2 (cz. 1)", "§ 2 (cz. 2)");
        Assert.Equal([2, 3], AnalysisFollowUp.FindReferencedUnits("co z § 2?", units));
    }

    [Fact] // rodzaje jednostek nie mieszają się: „art. 2" nie pasuje do nagłówka „§ 2"
    public void Reference_kind_must_match()
    {
        var units = Units("§ 1", "§ 2", "§ 3");
        Assert.Empty(AnalysisFollowUp.FindReferencedUnits("co mówi art. 2 tej ustawy?", units));
    }

    [Fact]
    public void SelectByEmbedding_returns_topK_in_document_order()
    {
        float[] query = [1f, 0f];
        IReadOnlyList<float[]> embeddings =
        [
            [0.1f, 0.9f],   // jednostka 1 — daleko
            [0.9f, 0.1f],   // jednostka 2 — blisko
            [0.95f, 0.05f], // jednostka 3 — najbliżej
        ];

        var picked = AnalysisFollowUp.SelectByEmbedding(query, embeddings, topK: 2);

        Assert.Equal([2, 3], picked); // top-2 po podobieństwie, kolejność dokumentu
    }

    [Fact]
    public void ComposeAnchorTurn_puts_selected_units_first_and_respects_budget()
    {
        var units = Units("§ 1", "§ 2", "§ 3");
        var turn = AnalysisFollowUp.ComposeAnchorTurn(Snap(units), [2]);

        Assert.Equal("oceń ryzyka", turn.Question);
        Assert.NotNull(turn.Answer);
        Assert.Contains("§ 2 — RYZYKO", turn.Answer);
        Assert.Contains("Uzasadnienie dla § 2", turn.Answer);
        Assert.Contains("Treść postanowienia", turn.Answer);        // treść jednostki, nie tylko werdykt
        Assert.Contains("Streszczenie: Umowa ma jedno ryzyko", turn.Answer);
        Assert.Contains("§ 1 — OK; § 2 — RYZYKO; § 3 — OK", turn.Answer); // tabela przekrojowa
        Assert.True(turn.Answer.IndexOf("§ 2 — RYZYKO") < turn.Answer.IndexOf("Streszczenie:")); // wybrane z przodu
        Assert.True(turn.Answer.Length <= GroundedPrompt.MaxHistoryAnswerChars + 1);
        Assert.Equal(["art. 2 KC"], turn.SourceAnchors!); // kotwice źródeł TYLKO wybranych jednostek
    }

    [Fact] // pytanie przekrojowe (bez selekcji): kotwica = streszczenie + tabela werdyktów
    public void ComposeAnchorTurn_cross_cutting_has_table_without_unit_details()
    {
        var units = Units("§ 1", "§ 2", "§ 3");
        var turn = AnalysisFollowUp.ComposeAnchorTurn(Snap(units), []);

        Assert.Contains("Werdykty: § 1 — OK", turn.Answer);
        Assert.DoesNotContain("Treść:", turn.Answer);
        Assert.Null(turn.SourceAnchors);
    }

    [Fact] // bardzo długie uzasadnienia/streszczenie → twarde cięcie do budżetu historii
    public void ComposeAnchorTurn_truncates_to_history_budget()
    {
        var units = Units("§ 1", "§ 2", "§ 3");
        var snap = Snap(units,
            summary: new string('s', 3000),
            result: u => new UnitAnalysis(u.Index, u.Heading, UnitVerdict.Risk, new string('a', 2000), []));

        var turn = AnalysisFollowUp.ComposeAnchorTurn(snap, [1, 2]);

        Assert.True(turn.Answer!.Length <= GroundedPrompt.MaxHistoryAnswerChars + 1);
    }

    [Fact] // jednostka jeszcze w toku (Result=null) → kotwica z samą treścią, bez werdyktu
    public void ComposeAnchorTurn_handles_pending_unit()
    {
        var units = Units("§ 1", "§ 2");
        var snap = Snap(units, result: u => u.Index == 1
            ? new UnitAnalysis(1, "§ 1", UnitVerdict.Ok, "Uzasadnienie [1].", [])
            : null);

        var turn = AnalysisFollowUp.ComposeAnchorTurn(snap, [2]);

        Assert.Contains("Treść postanowienia", turn.Answer);
        Assert.DoesNotContain("§ 2 —", turn.Answer);
    }
}
