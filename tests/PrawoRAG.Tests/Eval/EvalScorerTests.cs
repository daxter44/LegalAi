using PrawoRAG.Eval;

namespace PrawoRAG.Tests.Eval;

/// <summary>
/// Testy czystej logiki scoringu E5 (bez DB/LLM). Dowodzą, że metryki liczą się poprawnie —
/// harness może się na nich opierać, a golden set/korpus dokładamy jako dane.
/// </summary>
public class EvalScorerTests
{
    private static GoldenItem InCorpus(string? eli = "DU/1997/553", string? art = "148") => new()
    {
        Id = "x", Question = "q", Category = GoldenCategory.InCorpus, ShouldAbstain = false,
        ExpectedEli = eli, ExpectedArticle = art,
    };

    private static ItemObservation Obs(bool wouldAbstain, double sim, params RetrievedLocator[] retrieved) => new()
    {
        Id = "x", MaxSimilarity = sim, WouldAbstain = wouldAbstain, Retrieved = retrieved,
    };

    [Fact]
    public void Retrieval_hit_when_expected_locator_present()
    {
        var v = EvalScorer.Score(InCorpus(), Obs(false, 0.8,
            new RetrievedLocator("DU/1964/93", "415", null),   // dystraktor
            new RetrievedLocator("DU/1997/553", "148", null))); // trafienie
        Assert.True(v.RetrievalHit);
        Assert.True(v.AbstentionCorrect);
    }

    [Fact]
    public void Retrieval_miss_when_expected_locator_absent()
    {
        var v = EvalScorer.Score(InCorpus(), Obs(false, 0.8,
            new RetrievedLocator("DU/1964/93", "415", null)));
        Assert.False(v.RetrievalHit);
    }

    [Fact]
    public void Abstention_incorrect_when_answering_out_of_corpus()
    {
        var item = new GoldenItem { Id = "o", Question = "q", Category = GoldenCategory.OutOfCorpus, ShouldAbstain = true };
        Assert.False(EvalScorer.Score(item, Obs(wouldAbstain: false, 0.8)).AbstentionCorrect); // powinien odmówić, a nie odmówił
        Assert.True(EvalScorer.Score(item, Obs(wouldAbstain: true, 0.3)).AbstentionCorrect);
    }

    [Fact]
    public void Trap_no_hallucination_true_when_abstained_false_when_dirty()
    {
        var trap = new GoldenItem { Id = "t", Question = "q", Category = GoldenCategory.Trap, ShouldAbstain = true };

        var vAbstained = EvalScorer.Score(trap, Obs(true, 0.3) with { Abstained = true, CitationsClean = null });
        Assert.True(vAbstained.NoHallucination); // odmówił → nie zmyślił

        var vDirty = EvalScorer.Score(trap, Obs(false, 0.8) with { Abstained = false, CitationsClean = false });
        Assert.False(vDirty.NoHallucination); // odpowiedział i zmyślił cytat
    }

    [Fact]
    public void NeedsLawyer_item_is_not_scored_for_recall()
    {
        var item = InCorpus() with { NeedsLawyer = true };
        Assert.Null(EvalScorer.Score(item, Obs(false, 0.8)).RetrievalHit);
    }

    [Fact]
    public void Aggregate_computes_metrics()
    {
        var verdicts = new[]
        {
            new ItemVerdict("a", GoldenCategory.InCorpus, 0.80, RetrievalHit: true, AbstentionCorrect: true, NoHallucination: null, ChatAbstentionCorrect: true),
            new ItemVerdict("b", GoldenCategory.InCorpus, 0.70, RetrievalHit: false, AbstentionCorrect: true, NoHallucination: null, ChatAbstentionCorrect: null),
            new ItemVerdict("c", GoldenCategory.OutOfCorpus, 0.40, RetrievalHit: null, AbstentionCorrect: true, NoHallucination: null, ChatAbstentionCorrect: true),
            new ItemVerdict("d", GoldenCategory.Trap, 0.90, RetrievalHit: null, AbstentionCorrect: false, NoHallucination: false, ChatAbstentionCorrect: false),
        };
        var r = EvalScorer.Aggregate(verdicts, 0.55);
        Assert.Equal(4, r.Total);
        Assert.Equal(0.5, r.RecallAtK);            // 1 z 2 scorowanych
        Assert.Equal(0.75, r.AbstentionAccuracy);  // 3 z 4 (bramka retrievalu)
        Assert.Equal(0.0, r.AntiHallucination);    // 1 pułapka, nie zaliczona
        Assert.Equal(2.0 / 3.0, r.ChatAbstentionAccuracy!.Value, 3); // 2 z 3 z czatem (a,c ok; d źle)
        Assert.Equal(3, r.ScoredChat);
    }

    [Fact]
    public void Chat_abstention_scored_end_to_end()
    {
        var outItem = new GoldenItem { Id = "o", Question = "q", Category = GoldenCategory.RelatedButWrong, ShouldAbstain = true };
        // LLM odmówił (Abstained=true) na pytaniu wymagającym abstynencji → poprawnie.
        Assert.True(EvalScorer.Score(outItem, Obs(false, 0.9) with { Abstained = true }).ChatAbstentionCorrect);
        // LLM odpowiedział (Abstained=false) mimo że powinien odmówić → niepoprawnie.
        Assert.False(EvalScorer.Score(outItem, Obs(false, 0.9) with { Abstained = false }).ChatAbstentionCorrect);
        // Czat nieuruchomiony (Abstained=null) → nie scorujemy.
        Assert.Null(EvalScorer.Score(outItem, Obs(false, 0.9)).ChatAbstentionCorrect);
    }

    [Fact]
    public void BestThreshold_separates_when_distributions_disjoint()
    {
        var data = new (bool, double)[]
        {
            (false, 0.85), (false, 0.82),  // w korpusie — nie abstynencja
            (true, 0.40), (true, 0.35),    // poza — abstynencja
        };
        var (t, acc) = EvalScorer.BestThreshold(data);
        Assert.Equal(1.0, acc);            // istnieje próg rozdzielający idealnie
        Assert.InRange(t, 0.40, 0.85);
    }

    [Fact]
    public void BestThreshold_low_accuracy_when_distributions_overlap()
    {
        var data = new (bool, double)[]
        {
            (false, 0.78), (false, 0.79),  // w korpusie
            (true, 0.77), (true, 0.80),    // poza — nakładają się
        };
        var (_, acc) = EvalScorer.BestThreshold(data);
        Assert.True(acc < 1.0, "nakładające się rozkłady nie powinny dać idealnego progu (dowód potrzeby rerankera)");
    }
}
