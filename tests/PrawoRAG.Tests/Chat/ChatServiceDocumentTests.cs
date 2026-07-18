using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using PrawoRAG.Api.Services;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Tests.Fakes;

namespace PrawoRAG.Tests.Chat;

/// <summary>
/// T-DOC-4 — ChatService z załącznikiem (fakes, bez DB/LLM/sieci): fragmenty [D] w prompcie
/// i w zdarzeniu DocSourcesEvent; bez dokumentu zachowanie identyczne jak dotąd; abstynencja
/// korpusu ma pierwszeństwo — dokument nie zamienia odmowy w odpowiedź (decyzja #5 planu DOC).
/// </summary>
public class ChatServiceDocumentTests
{
    private static RetrievedChunk Chunk(string text) => new()
    {
        ChunkId = Guid.NewGuid(), DocumentId = Guid.NewGuid(), Text = text,
        Source = "ELI", DocType = DocTypes.Act, Title = "Kodeks cywilny", Score = 1.0,
    };

    private sealed class FixedRetriever(double signal) : IRetriever
    {
        public Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, CancellationToken ct) =>
            Task.FromResult(new RetrievalResult([Chunk("Art. 484. Kara umowna…")], signal));
    }

    private sealed class NoOpAugmenter : ITemporalAugmenter
    {
        public Task<IReadOnlyList<RetrievedChunk>> AugmentAsync(
            RetrievalQuery query, IReadOnlyList<RetrievedChunk> retrieved, CancellationToken ct)
            => Task.FromResult(retrieved);
    }

    private sealed class FakeLlm(string answer) : ILlmProvider
    {
        public LlmRequest? LastRequest { get; private set; }
        public string ModelId => "fake";

        public async IAsyncEnumerable<string> StreamCompletionAsync(
            LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            LastRequest = request;
            yield return answer;
            await Task.CompletedTask;
        }
    }

    /// <summary>Dokument z embeddingami z TEGO SAMEGO fake-providera co ChatService — wymiary
    /// muszą się zgadzać z embeddingiem zapytania (jak w produkcji: jeden model dla obu stron).</summary>
    private static async Task<DocumentContext> DocAsync(params string[] fragments) => new()
    {
        FileName = "umowa.pdf", PageCount = 1, Truncated = false,
        Chunks = fragments,
        Embeddings = await new FakeEmbeddingProvider().EmbedPassagesAsync(fragments, default),
    };

    private static ChatService Service(IRetriever retriever, ILlmProvider llm) =>
        new(retriever, new NoOpAugmenter(), llm, Options.Create(new RetrievalOptions()), new FakeEmbeddingProvider());

    private static async Task<List<ChatEvent>> Drain(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Document_fragments_enter_prompt_and_events()
    {
        var llm = new FakeLlm("Kara podlega miarkowaniu [1], co potwierdza §7 [D1].");
        var events = await Drain(Service(new FixedRetriever(0.9), llm)
            .AskAsync("czy §7 jest ważny?", [], await DocAsync("§7. Kara umowna 500 zł/dzień."), default));

        var docEvt = Assert.IsType<DocSourcesEvent>(events.First(e => e is DocSourcesEvent));
        Assert.Equal("umowa.pdf", docEvt.FileName);
        Assert.Equal(1, Assert.Single(docEvt.Fragments).Index);

        Assert.Contains("[D1] §7. Kara umowna", llm.LastRequest!.Messages[^1].Content); // sekcja DOKUMENT
        Assert.Contains("ZAŁĄCZNIK", llm.LastRequest.Messages[0].Content);              // DocumentRules w systemie

        var done = Assert.IsType<DoneEvent>(events[^1]);
        Assert.True(done.Check!.IsClean); // [1] i [D1] w zakresach
        Assert.Equal([1], done.Check.DocCited);
    }

    [Fact]
    public async Task Without_document_no_doc_event_and_plain_system()
    {
        var llm = new FakeLlm("Odpowiedź [1].");
        var events = await Drain(Service(new FixedRetriever(0.9), llm)
            .AskAsync("pytanie", [], null, default));

        Assert.DoesNotContain(events, e => e is DocSourcesEvent);
        Assert.DoesNotContain("ZAŁĄCZNIK", llm.LastRequest!.Messages[0].Content);
    }

    [Fact] // słaby sygnał korpusu → odmowa MIMO załącznika (fakty bez prawa = odmowa jak dotąd)
    public async Task Corpus_abstention_wins_over_document()
    {
        var events = await Drain(Service(new FixedRetriever(0.10), new FakeLlm("nie powinno paść"))
            .AskAsync("pytanie", [], await DocAsync("§7. Kara umowna."), default));

        Assert.Contains(events, e => e is AbstainEvent);
        Assert.DoesNotContain(events, e => e is DocSourcesEvent); // LLM nie wołany, fragmenty nie liczone
    }

    [Fact] // fabrykacja w przestrzeni D: [D2] przy jednym fragmencie → Check nieczysty
    public async Task Doc_citation_out_of_range_flagged()
    {
        var llm = new FakeLlm("Rzekomy zapis [D2] przewiduje karę.");
        var events = await Drain(Service(new FixedRetriever(0.9), llm)
            .AskAsync("pytanie", [], await DocAsync("§7. Kara umowna."), default));

        var done = Assert.IsType<DoneEvent>(events[^1]);
        Assert.False(done.Check!.IsClean);
        Assert.Contains(2, done.Check.DocOutOfRange!);
    }
}
