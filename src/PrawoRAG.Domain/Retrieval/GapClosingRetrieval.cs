using PrawoRAG.Domain.Llm;

namespace PrawoRAG.Domain.Retrieval;

/// <summary>
/// Retrieval domykający lukę (Zadanie 12 planu ROU) — JEDNO wejście retrievalu dla czatu,
/// endpointu SSE i evalu. Gdy pierwsza runda nie daje pokrycia, zamiast od razu odmawiać
/// pytamy bazę PONOWNIE, zapytaniem przełożonym na terminologię ustawową.
///
/// Dwie własności, które czynią to bezpiecznym i tanim — i które muszą tu zostać:
///
/// 1. **Może tylko DODAĆ kontekst, nigdy nie obniża poprzeczki.** Bramka abstynencji i walidacja
///    cytatów działają na końcu, bez zmian. Zamienia więc „odmowę" na „odmowę po drugiej próbie"
///    albo na odpowiedź ugruntowaną — ZERO nowego trybu halucynacji. To zasadnicza różnica względem
///    tool callingu, który wprowadza nowy tryb awarii (model uznaje, że wie, i nie szuka).
///
/// 2. **Koszt płacony WYŁĄCZNIE na pytaniach, które dziś nie dają nic.** Udana pierwsza runda nie
///    zwalnia ani o milisekundę, bo reformulator nie jest wtedy w ogóle wołany. Dlatego 35 s
///    rerankera tego mechanizmu nie blokuje, mimo że blokuje pętlę narzędzia z Fazy 5.
///
/// Blizna, której tu pilnujemy: commit <c>1de510b</c> („jeden FollowUpSelector zamiast trzech
/// rozjechanych kopii") — „rozjazd kopii = rozjazd metryki". Dlatego eval MUSI wołać to samo wejście
/// co czat, inaczej metryka odmów mierzyłaby pipeline, którego nikt nie używa.
/// </summary>
public static class GapClosingRetrieval
{
    /// <param name="Selection">Wybrany wariant zapytania i jego wynik (jak dotąd).</param>
    /// <param name="ExtraRound">Czy wykonano dodatkową rundę retrievalu.</param>
    /// <param name="ReformulatedQuery">Zapytanie użyte w drugiej rundzie (do UI i diagnostyki).</param>
    public sealed record Outcome(
        FollowUpSelector.Selection Selection, bool ExtraRound = false, string? ReformulatedQuery = null)
    {
        public RetrievalQuery Query => Selection.Query;
        public RetrievalResult Result => Selection.Result;
    }

    /// <summary>
    /// Runda 1 = dzisiejszy <see cref="FollowUpSelector"/>. Jeśli bramka abstynencji odmówiłaby na
    /// jej wyniku, a reformulator zaproponuje INNE zapytanie — runda 2, a wyniki są scalane.
    /// </summary>
    /// <param name="reformulator">Null = mechanizm wyłączony (eval bez modelu pomocniczego, testy).</param>
    /// <param name="maxExtraRounds">0 = zachowanie jak przed Fazą 4 (tylko runda 1).</param>
    public static async Task<Outcome> RetrieveAsync(
        IRetriever retriever,
        Func<string, RetrievalQuery> queryFactory,
        string question,
        IReadOnlyList<ChatTurn> history,
        double cosineMargin,
        double rerankMargin,
        double abstentionThreshold,
        IQueryReformulator? reformulator,
        int maxExtraRounds,
        CancellationToken ct)
    {
        var first = await FollowUpSelector.SelectAsync(
            retriever, queryFactory, question, history, cosineMargin, rerankMargin, ct);

        if (maxExtraRounds <= 0 || reformulator is null) return new Outcome(first);

        // Druga runda TYLKO wtedy, gdy pierwsza i tak skończyłaby się odmową — inaczej płacilibyśmy
        // ~40 s za pytania, które już mają dobrą odpowiedź.
        if (!AbstentionPolicy.ShouldAbstain(first.Result, abstentionThreshold)) return new Outcome(first);

        // Reformulator dostaje HISTORIĘ (2026-08-27). Bez niej na follow-upie przekładał na
        // terminologię ustawową samo „a co z § 2?" — tekst bez tematu — czyli w klasie tur,
        // w której odmowy są najczęstsze, druga runda była z góry przegrana.
        var reformulated = await reformulator.ReformulateAsync(question, history, ct);
        if (reformulated is null) return new Outcome(first); // brak sensownego wariantu → dzisiejsza odmowa

        // Historia PUSTA, mimo że runda 1 ją dostała: przeformułowane zapytanie jest już samodzielne
        // (reformulator widział rozmowę i rozwiązał odwołanie), więc sklejanie go z historią
        // w FollowUpSelector tylko rozmyłoby embedding i podwoiło koszt rundy — dwa retrievale
        // zamiast jednego, na tekście gorszym niż to, co model właśnie napisał.
        // Skutek uboczny: zapytanie rundy 2 nie niesie już cytatów z historii, więc gdy wygra
        // sygnałem, augmenter nowelizacji dostanie właśnie je (patrz Merge) — cytat, którego user
        // sam nie wpisał, i tak nie powinien wyzwalać torów dokładnych.
        var second = await FollowUpSelector.SelectAsync(
            retriever, queryFactory, reformulated, [], cosineMargin, rerankMargin, ct);

        return new Outcome(Merge(first, second), ExtraRound: true, ReformulatedQuery: reformulated);
    }

    /// <summary>
    /// Scala wyniki obu rund. Zapytanie zostaje z rundy, która wygrała sygnałem — to ono zasila
    /// augmenter nowelizacji (niesie cytaty), więc nie może być zapytaniem „obok".
    ///
    /// Kolejność: fragmenty rundy o LEPSZYM sygnale idą pierwsze, dedup po identyfikatorze fragmentu,
    /// a całość przycięta do <c>TopK</c> zapytania — inaczej prompt puchnie i mielibyśmy dwa razy
    /// więcej kontekstu niż kalibrowano.
    /// </summary>
    private static FollowUpSelector.Selection Merge(
        FollowUpSelector.Selection first, FollowUpSelector.Selection second)
    {
        var secondWins = Signal(second.Result) > Signal(first.Result);
        var (better, worse) = secondWins ? (second, first) : (first, second);

        var merged = new List<RetrievedChunk>();
        var seen = new HashSet<Guid>();
        foreach (var chunk in better.Result.Chunks.Concat(worse.Result.Chunks))
            if (seen.Add(chunk.ChunkId))
                merged.Add(chunk);

        var topK = better.Query.TopK;
        if (topK > 0 && merged.Count > topK) merged = merged[..topK];

        var result = new RetrievalResult(
            merged,
            // Sygnały bierzemy MAKSIMUM z obu rund: bramka pyta „czy mamy pokrycie", a mamy je
            // wtedy, gdy KTÓRAKOLWIEK runda je znalazła.
            Math.Max(first.Result.MaxSimilarity, second.Result.MaxSimilarity),
            Max(first.Result.RerankTopScore, second.Result.RerankTopScore),
            first.Result.ExactMatchHits + second.Result.ExactMatchHits);

        return new FollowUpSelector.Selection(better.Query, result, better.UsedContextual);
    }

    /// <summary>Sygnał rundy: cross-encoder, gdy jest (lepszy do rankingu), inaczej cosine.</summary>
    private static double Signal(RetrievalResult r) => r.RerankTopScore ?? r.MaxSimilarity;

    private static double? Max(double? a, double? b) =>
        a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}
