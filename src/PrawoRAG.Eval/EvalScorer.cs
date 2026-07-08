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

        // Abstynencja END-TO-END (realna bramka): gdy czat był uruchomiony (obs.Abstained != null), poprawne
        // zachowanie = odmówił ⇔ ShouldAbstain. To mierzy LLM jako bramkę, nie próg z retrievalu.
        bool? chatAbstentionCorrect = null;
        if (!item.NeedsLawyer && obs.Abstained is not null)
            chatAbstentionCorrect = obs.Abstained.Value == item.ShouldAbstain;

        // Świeżość (AKT): dla pozycji Freshness z oczekiwaną nowelą — czy nowela jest wśród źródeł PO augmentacji.
        // Obiektywne (obecność), nie merytoryczne („zestawienie" ocenia prawnik).
        bool? freshnessHit = null;
        if (item.Category == GoldenCategory.Freshness && item.ExpectedAmendmentEli is not null)
            freshnessHit = obs.Retrieved.Any(r =>
                string.Equals(r.Eli, item.ExpectedAmendmentEli, StringComparison.OrdinalIgnoreCase));

        return new ItemVerdict(item.Id, item.Category, obs.MaxSimilarity, hit, abstentionCorrect, noHallucination, chatAbstentionCorrect, freshnessHit);
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
        var chat = verdicts.Where(v => v.ChatAbstentionCorrect is not null).ToList();
        var fresh = verdicts.Where(v => v.FreshnessHit is not null).ToList();
        // Freshness dzieli z InCorpus oczekiwanie „mamy odpowiedź" (nie abstynencja) → do puli „w korpusie".
        var inSims = verdicts.Where(v => v.Category is GoldenCategory.InCorpus or GoldenCategory.Freshness).Select(v => v.MaxSimilarity).ToList();
        var outSims = verdicts.Where(v => v.Category is not (GoldenCategory.InCorpus or GoldenCategory.Freshness)).Select(v => v.MaxSimilarity).ToList();

        return new EvalReport
        {
            Total = verdicts.Count,
            Threshold = threshold,
            RecallAtK = withHit.Count == 0 ? null : withHit.Count(v => v.RetrievalHit == true) / (double)withHit.Count,
            AbstentionAccuracy = verdicts.Count == 0 ? 0 : verdicts.Count(v => v.AbstentionCorrect) / (double)verdicts.Count,
            AntiHallucination = traps.Count == 0 ? null : traps.Count(v => v.NoHallucination == true) / (double)traps.Count,
            ChatAbstentionAccuracy = chat.Count == 0 ? null : chat.Count(v => v.ChatAbstentionCorrect == true) / (double)chat.Count,
            FreshnessRecall = fresh.Count == 0 ? null : fresh.Count(v => v.FreshnessHit == true) / (double)fresh.Count,
            MeanSimInCorpus = inSims.Count == 0 ? 0 : inSims.Average(),
            MeanSimOutOfCorpus = outSims.Count == 0 ? 0 : outSims.Average(),
            ScoredRecall = withHit.Count,
            ScoredTraps = traps.Count,
            ScoredChat = chat.Count,
            ScoredFreshness = fresh.Count,
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
