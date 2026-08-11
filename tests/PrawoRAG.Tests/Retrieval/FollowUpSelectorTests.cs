using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Tests.Fakes;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// T-FUS — jedyna orkiestracja follow-upu (dawniej trzy kopie: ChatService, /api/chat, RefusalEvalRunner,
/// z których ta trzecia zdążyła się rozjechać). Bez DB/TEI: <see cref="FakeRetriever"/> odpowiada po
/// tekście zapytania, więc asercje dotyczą TEGO, co jest tu naprawdę logiką — jakie teksty trafiają do
/// jakich torów i który wariant wygrywa.
/// </summary>
public class FollowUpSelectorTests
{
    private const string Q1 = "Co grozi za wyciek danych osobowych z systemów medycznych?";
    private const string Q2 = "A co powinienem zrobić, jeżeli do wycieku doszło?";

    private static readonly IReadOnlyList<ChatTurn> History =
        [new(Q1, "Źródła nie określają sankcji. Podmioty prowadzące bazy danych w ochronie zdrowia…",
            ["Ustawa o systemie informacji w ochronie zdrowia, art. 37"])];

    private static RetrievalQuery Factory(string text) => new() { Text = text, TopK = 8 };

    [Fact] // Pusta historia = jeden retrieval, bez wariantu kontekstowego (zero kosztu dla zwykłych pytań).
    public async Task No_history_retrieves_once_and_uses_raw()
    {
        var retriever = new FakeRetriever(_ => new RetrievalResult([], 0.80, 0.90));
        var sel = await FollowUpSelector.SelectAsync(retriever, Factory, Q1, [],
            FollowUpQuery.DefaultSignalMargin, FollowUpQuery.DefaultRerankSignalMargin, default);

        Assert.Single(retriever.Queries);
        Assert.False(sel.UsedContextual);
        Assert.Equal(Q1, sel.Query.Text);
    }

    [Fact] // Wariant kontekstowy dostaje fold w Text, ale surowe pytanie w RerankText i ExactMatchText.
    public async Task Contextual_query_keeps_raw_question_for_judge_and_exact_lanes()
    {
        var retriever = new FakeRetriever(_ => new RetrievalResult([], 0.80, 0.90));
        await FollowUpSelector.SelectAsync(retriever, Factory, Q2, History,
            FollowUpQuery.DefaultSignalMargin, FollowUpQuery.DefaultRerankSignalMargin, default);

        var ctx = retriever.Queries[1];
        Assert.Contains("art. 37", ctx.Text);                 // fold zostaje w torze gęstym/BM25
        Assert.Equal(Q2, ctx.RerankText);                     // sędzia sądzi po pytaniu użytkownika
        Assert.DoesNotContain("art. 37", ctx.EffectiveExactMatchText); // tory dokładne bez foldu
    }

    [Fact] // Zmierzony przypadek: fold ma wyższy cosine, ale reranker go demaskuje → wygrywa surowy.
    public async Task Misleading_fold_loses_on_rerank_signal()
    {
        var retriever = new FakeRetriever(q => q.RerankText is null
            ? new RetrievalResult([], 0.8431, 0.8842)   // surowy
            : new RetrievalResult([], 0.8576, 0.0503)); // kontekstowy

        var sel = await FollowUpSelector.SelectAsync(retriever, Factory, Q2, History,
            FollowUpQuery.DefaultSignalMargin, FollowUpQuery.DefaultRerankSignalMargin, default);

        Assert.False(sel.UsedContextual);
        Assert.Equal(Q2, sel.Query.Text);
    }

    [Fact] // Bez rerankera decyduje cosine — dokładnie dzisiejsze zachowanie (i dzisiejszy bug).
    public async Task Without_reranker_keeps_cosine_behaviour()
    {
        var retriever = new FakeRetriever(q => q.RerankText is null
            ? new RetrievalResult([], 0.8431)
            : new RetrievalResult([], 0.8576));

        var sel = await FollowUpSelector.SelectAsync(retriever, Factory, Q2, History,
            FollowUpQuery.DefaultSignalMargin, FollowUpQuery.DefaultRerankSignalMargin, default);

        Assert.True(sel.UsedContextual);
    }
}
