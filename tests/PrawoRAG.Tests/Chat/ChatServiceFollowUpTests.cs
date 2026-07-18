using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using PrawoRAG.Api.Services;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Tests.Chat;

/// <summary>
/// Polityka follow-upów w ChatService (fakes, bez DB/LLM/sieci): przy historii retrieval liczony 2x
/// (surowe pytanie vs sklejone z poprzednimi pytaniami), wygrywa silniejszy sygnał; bez historii —
/// dokładnie 1 retrieval (zero regresji jednoturowej); abstynencja liczona z WYBRANEGO wyniku;
/// augmenter dostaje efektywne zapytanie; prompt zawiera historię.
/// </summary>
public class ChatServiceFollowUpTests
{
    private const double Threshold = 0.55;

    private static RetrievedChunk Chunk(string text) => new()
    {
        ChunkId = Guid.NewGuid(), DocumentId = Guid.NewGuid(), Text = text,
        Source = "ELI", DocType = DocTypes.Act, Title = "KPC", Score = 1.0,
    };

    /// <summary>Fake retrievera: sygnał zależny od tekstu zapytania; zlicza wywołania.</summary>
    private sealed class FakeRetriever(Func<string, double> signal) : IRetriever
    {
        public List<string> Queries { get; } = [];

        public Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, CancellationToken ct)
        {
            Queries.Add(query.Text);
            var s = signal(query.Text);
            return Task.FromResult(new RetrievalResult([Chunk($"wynik dla: {query.Text}")], s));
        }
    }

    private sealed class NoOpAugmenter : ITemporalAugmenter
    {
        public string? LastQueryText { get; private set; }

        // Kontrakt AKT-4b: augmenter zwraca CAŁĄ (zastępczą) listę; brak nowel → wejście bez zmian.
        public Task<IReadOnlyList<RetrievedChunk>> AugmentAsync(
            RetrievalQuery query, IReadOnlyList<RetrievedChunk> retrieved, CancellationToken ct)
        {
            LastQueryText = query.Text;
            return Task.FromResult(retrieved);
        }
    }

    private sealed class FakeLlm : ILlmProvider
    {
        public LlmRequest? LastRequest { get; private set; }
        public string ModelId => "fake";

        public async IAsyncEnumerable<string> StreamCompletionAsync(
            LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            LastRequest = request;
            yield return "Odpowiedź [1].";
            request.OnUsage?.Invoke(new LlmUsage(100, 20, Estimated: false)); // jak provider po strumieniu
            await Task.CompletedTask;
        }
    }

    private static ChatService Service(FakeRetriever retriever, NoOpAugmenter augmenter, FakeLlm llm) =>
        new(retriever, augmenter, llm, Options.Create(new RetrievalOptions { AbstentionThreshold = Threshold }),
            new Fakes.FakeEmbeddingProvider());

    private static async Task<List<ChatEvent>> Drain(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Without_history_single_retrieval_no_regression()
    {
        var retriever = new FakeRetriever(_ => 0.9);
        var (augmenter, llm) = (new NoOpAugmenter(), new FakeLlm());

        var events = await Drain(Service(retriever, augmenter, llm).AskAsync("pytanie", [], null, default));

        Assert.Equal(["pytanie"], retriever.Queries); // dokładnie 1 retrieval, surowe pytanie
        Assert.Contains(events, e => e is SourcesEvent);
        Assert.Equal(2, llm.LastRequest!.Messages.Count); // System + User (bez historii)

        // Usage z providera przechodzi do DoneEvent (widoczność w UI steruje osobna flaga).
        var done = Assert.IsType<DoneEvent>(events[^1]);
        Assert.Equal(new LlmUsage(100, 20, false), done.Usage);
    }

    [Fact]
    public async Task Weak_followup_rescued_by_contextual_retrieval()
    {
        // Surowe „a co z § 2?" → sygnał 0.30 (odmowa); sklejone z poprzednim pytaniem → 0.80.
        var retriever = new FakeRetriever(q => q.Contains("art. 367") ? 0.80 : 0.30);
        var (augmenter, llm) = (new NoOpAugmenter(), new FakeLlm());
        var history = new[] { new ChatTurn("co mówi art. 367 KPC?", "Art. 367 stanowi…") };

        var events = await Drain(Service(retriever, augmenter, llm).AskAsync("a co z § 2?", history, null, default));

        Assert.Equal(2, retriever.Queries.Count);
        Assert.Equal("co mówi art. 367 KPC? a co z § 2?", retriever.Queries[1]); // sklejony wariant
        Assert.DoesNotContain(events, e => e is AbstainEvent);   // uratowane przed fałszywą odmową
        Assert.Equal("co mówi art. 367 KPC? a co z § 2?", augmenter.LastQueryText); // augmenter widzi kontekst
        Assert.Contains(llm.LastRequest!.Messages, m => m.Role == ChatRole.Assistant); // historia w prompcie
        var final = llm.LastRequest.Messages[^1];
        Assert.Contains("a co z § 2?", final.Content); // do promptu idzie ORYGINALNE pytanie
    }

    [Fact]
    public async Task Topic_switch_raw_question_wins()
    {
        // Nowy temat: surowe pytanie retrievuje mocniej O WIĘCEJ NIŻ MARGINES — brak skażenia starym tematem.
        var retriever = new FakeRetriever(q => q.StartsWith("jaka kara") ? 0.85 : 0.60);
        var (augmenter, llm) = (new NoOpAugmenter(), new FakeLlm());
        var history = new[] { new ChatTurn("co mówi art. 367 KPC?", "Art. 367 stanowi…") };

        await Drain(Service(retriever, augmenter, llm).AskAsync("jaka kara grozi za zabójstwo?", history, null, default));

        Assert.Equal(2, retriever.Queries.Count);
        Assert.Equal("jaka kara grozi za zabójstwo?", augmenter.LastQueryText); // wygrało surowe
    }

    [Fact]
    public async Task Noise_level_raw_advantage_does_not_beat_contextual()
    {
        // Regresja z M4: surowe „a co z § 2?" miało cosine 0.879008 do PRZYPADKOWYCH fragmentów,
        // kontekstowe 0.879000 do właściwego art. 367 — różnica 8e-6 to szum, a ostre `>` wybierało
        // gorszy surowy wariant. Z marginesem wygrywa kontekstowe.
        var retriever = new FakeRetriever(q => q.Contains("art. 367") ? 0.879000 : 0.879008);
        var (augmenter, llm) = (new NoOpAugmenter(), new FakeLlm());
        var history = new[] { new ChatTurn("co mówi art. 367 KPC?", "Art. 367 stanowi…") };

        await Drain(Service(retriever, augmenter, llm).AskAsync("a co z § 2?", history, null, default));

        Assert.Equal("co mówi art. 367 KPC? a co z § 2?", augmenter.LastQueryText); // kontekstowe mimo niższego sygnału
    }

    [Fact]
    public async Task Abstains_when_both_signals_weak()
    {
        var retriever = new FakeRetriever(_ => 0.20);
        var history = new[] { new ChatTurn("pytanie", "odpowiedź") };

        var events = await Drain(Service(retriever, new NoOpAugmenter(), new FakeLlm())
            .AskAsync("dopytanie", history, null, default));

        Assert.Contains(events, e => e is AbstainEvent);
        Assert.Contains(events, e => e is DoneEvent { Abstained: true });
    }
}
