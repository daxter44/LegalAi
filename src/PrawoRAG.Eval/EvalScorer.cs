namespace PrawoRAG.Eval;

/// <summary>
/// Czysta logika scoringu (bez DB/LLM — w pełni testowalna jednostkowo). Porównuje OCZEKIWANE
/// zachowanie (GoldenItem) z tym, co system zrobił (ItemObservation), i agreguje metryki.
/// </summary>
public static class EvalScorer
{
    public static ItemVerdict Score(GoldenItem item, ItemObservation obs)
    {
        // Recall: tylko dla pozycji z obiektywnym oczekiwanym lokalizatorem (i nie-abstynencyjnych).
        bool? hit = null;
        if (!item.ShouldAbstain && !item.NeedsLawyer && HasExpected(item))
            hit = obs.Retrieved.Any(r => Matches(item, r));

        var abstentionCorrect = item.ShouldAbstain == obs.WouldAbstain;

        // Anty-halucynacja: pułapkę zaliczamy, gdy system odmówił ALBO cytaty są czyste (nie zmyślił).
        bool? noHallucination = null;
        if (item.Category == GoldenCategory.Trap && (obs.CitationsClean is not null || obs.Abstained is not null))
            noHallucination = (obs.Abstained ?? false) || (obs.CitationsClean ?? false);

        return new ItemVerdict(item.Id, item.Category, obs.MaxSimilarity, hit, abstentionCorrect, noHallucination);
    }

    private static bool HasExpected(GoldenItem i) =>
        i.ExpectedEli is not null || i.ExpectedArticle is not null || i.ExpectedCaseNumber is not null;

    private static bool Matches(GoldenItem item, RetrievedLocator r)
    {
        if (item.ExpectedCaseNumber is not null)
            return Norm(r.CaseNumber) == Norm(item.ExpectedCaseNumber);

        var eliOk = item.ExpectedEli is null ||
            string.Equals(r.Eli, item.ExpectedEli, StringComparison.OrdinalIgnoreCase);
        var artOk = item.ExpectedArticle is null ||
            string.Equals(r.Article, item.ExpectedArticle, StringComparison.OrdinalIgnoreCase);
        return eliOk && artOk;
    }

    private static string? Norm(string? s) =>
        s is null ? null : string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    public static EvalReport Aggregate(IReadOnlyList<ItemVerdict> verdicts, double threshold)
    {
        var withHit = verdicts.Where(v => v.RetrievalHit is not null).ToList();
        var traps = verdicts.Where(v => v.NoHallucination is not null).ToList();
        var inSims = verdicts.Where(v => v.Category == GoldenCategory.InCorpus).Select(v => v.MaxSimilarity).ToList();
        var outSims = verdicts.Where(v => v.Category != GoldenCategory.InCorpus).Select(v => v.MaxSimilarity).ToList();

        return new EvalReport
        {
            Total = verdicts.Count,
            Threshold = threshold,
            RecallAtK = withHit.Count == 0 ? null : withHit.Count(v => v.RetrievalHit == true) / (double)withHit.Count,
            AbstentionAccuracy = verdicts.Count == 0 ? 0 : verdicts.Count(v => v.AbstentionCorrect) / (double)verdicts.Count,
            AntiHallucination = traps.Count == 0 ? null : traps.Count(v => v.NoHallucination == true) / (double)traps.Count,
            MeanSimInCorpus = inSims.Count == 0 ? 0 : inSims.Average(),
            MeanSimOutOfCorpus = outSims.Count == 0 ? 0 : outSims.Average(),
            ScoredRecall = withHit.Count,
            ScoredTraps = traps.Count,
        };
    }

    /// <summary>
    /// Kalibracja progu (5.3): dla każdego kandydata liczy trafność abstynencji przy regule
    /// „abstynencja ⇔ MaxSimilarity &lt; próg" i zwraca próg maksymalizujący trafność na golden secie.
    /// Jeśli rozkłady „w korpusie"/„poza" się nakładają, najlepsza trafność będzie niska — to dowód,
    /// że surowy cosine nie wystarcza i trzeba rerankera (5.4).
    /// </summary>
    public static (double Threshold, double Accuracy) BestThreshold(
        IReadOnlyList<(bool ShouldAbstain, double MaxSim)> data, double lo = 0.30, double hi = 0.90, double step = 0.05)
    {
        (double t, double acc) best = (lo, -1);
        for (var t = lo; t <= hi + 1e-9; t += step)
        {
            var acc = data.Count == 0 ? 0 : data.Count(d => (d.MaxSim < t) == d.ShouldAbstain) / (double)data.Count;
            if (acc > best.acc) best = (Math.Round(t, 2), acc);
        }
        return best;
    }
}
