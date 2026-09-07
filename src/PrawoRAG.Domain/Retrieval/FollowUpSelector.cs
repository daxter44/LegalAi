using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain;

namespace PrawoRAG.Domain.Retrieval;

/// <summary>
/// JEDYNA orkiestracja follow-upu: podwójny retrieval (surowy vs kontekstowy) + wybór wariantu.
/// Istnieje, bo ta sama logika żyła w trzech kopiach (ChatService, endpoint /api/chat,
/// RefusalEvalRunner) i zdążyła się rozjechać — runner evalowy wołał starą przeciążkę bez foldu,
/// więc metryka mierzyła INNY pipeline niż produkcja, wbrew własnemu komentarzowi „rozjazd
/// z ChatService = rozjazd metryki". Kształt zapytań (TopK, filtry, MinChunkTokens) wstrzykuje
/// caller przez <paramref name="queryFactory"/> — każda ścieżka buduje je inaczej i to zostaje jej.
/// </summary>
public static class FollowUpSelector
{
    /// <summary>Wybrany wariant: zapytanie (do augmentera — niesie cytaty z historii), jego wynik
    /// i informacja, czy wygrał wariant kontekstowy (diagnostyka/eval).</summary>
    public sealed record Selection(RetrievalQuery Query, RetrievalResult Result, bool UsedContextual);

    public static async Task<Selection> SelectAsync(
        IRetriever retriever,
        Func<string, RetrievalQuery> queryFactory,
        string question,
        IReadOnlyList<ChatTurn> history,
        double cosineMargin,
        double rerankMargin,
        CancellationToken ct)
    {
        var rawQuery = queryFactory(question);
        // Prefiks etapów TYLKO przy follow-upie: bez historii jest jeden przebieg i „(1/2)" w UI byłoby
        // kłamstwem. Z historią użytkownik musi wiedzieć, że retrieval leci DWA razy — inaczej widzi
        // te same etapy dwukrotnie bez wyjaśnienia, dlaczego odpowiedź trwa dwa razy dłużej.
        if (history.Count > 0) rawQuery = rawQuery with { ProgressLabelPrefix = "(1/2) " };
        var rawResult = await LatencyLog.TimeAsync("retrieval.raw", () => retriever.RetrieveAsync(rawQuery, ct));
        if (history.Count == 0) return new Selection(rawQuery, rawResult, UsedContextual: false);

        // SEKWENCYJNIE — wspólny scoped DbContext nie jest thread-safe (nie zrównoleglać).
        var ctxQuery = queryFactory(FollowUpQuery.Contextualize(history, question)) with
        {
            // Tory DOKŁADNE: tylko pytania użytkownika — sygnatura/artykuł z ODPOWIEDZI systemu nie
            // może udawać jawnego asku (bug: kotwice wyroków zalewały TopK).
            ExactMatchText = FollowUpQuery.ContextualizeForExactMatch(history, question),
            // SĘDZIA: surowe pytanie — inaczej sklejka ocenia samą siebie (patrz RetrievalQuery.RerankText).
            RerankText = question,
            ProgressLabelPrefix = "(2/2) ",
        };
        // Follow-up: DRUGIE pełne wywołanie RetrieveAsync (sekwencyjnie, patrz komentarz wyżej) —
        // podwaja koszt każdego etapu toru (embedding, SQL, reranker) względem pytania bez historii.
        var ctxResult = await LatencyLog.TimeAsync("retrieval.contextual", () => retriever.RetrieveAsync(ctxQuery, ct));

        return FollowUpQuery.PickContextual(rawResult, ctxResult, cosineMargin, rerankMargin)
            ? new Selection(ctxQuery, ctxResult, UsedContextual: true)
            : new Selection(rawQuery, rawResult, UsedContextual: false);
    }
}
