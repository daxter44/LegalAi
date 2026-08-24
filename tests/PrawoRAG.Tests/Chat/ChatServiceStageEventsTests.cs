using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using PrawoRAG.Api.Services;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Tests.Fakes;

namespace PrawoRAG.Tests.Chat;

/// <summary>
/// T-STAGE-CHAT (Zadanie 3 planu ROU) — kanał zdarzeń z callbacków. <c>AskAsync</c> jest iteratorem
/// asynchronicznym, więc z callbacku (IProgress retrievalu, OnReasoningDelta providera) NIE DA SIĘ
/// zrobić <c>yield return</c>; oba źródła piszą do <c>Channel</c>, a pętla go drenuje. Te testy
/// pilnują, że (a) nic nie ginie, (b) kolejność zdarzeń odpowiada realnej kolejności pracy,
/// (c) etapy lecą także na ścieżce odmowy.
/// </summary>
public class ChatServiceStageEventsTests
{
    /// <summary>Retriever, który RAPORTUJE etapy (jak HybridRetriever) i zwraca ustalony sygnał.</summary>
    private sealed class StageReportingRetriever(double similarity, params string[] stages) : IRetriever
    {
        public Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, CancellationToken ct)
        {
            foreach (var s in stages) query.ReportStage(s, $"etykieta {s}", 42);
            var chunk = new RetrievedChunk
            {
                ChunkId = Guid.CreateVersion7(), DocumentId = Guid.CreateVersion7(),
                Text = "Art. 415 KC. Kto z winy swojej wyrządził szkodę…",
                Source = "ELI", DocType = DocTypes.Act, Title = "Kodeks cywilny",
                Score = 1.0, Similarity = similarity,
            };
            return Task.FromResult(new RetrievalResult([chunk], similarity));
        }
    }

    private sealed class NoOpAugmenter : ITemporalAugmenter
    {
        public Task<IReadOnlyList<RetrievedChunk>> AugmentAsync(
            RetrievalQuery query, IReadOnlyList<RetrievedChunk> retrieved, CancellationToken ct)
            => Task.FromResult(retrieved);
    }

    /// <summary>LLM emitujący PRZEPLOT: delty rozumowania (przez callback) i widoczne tokeny.</summary>
    private sealed class ThinkingLlm((string Text, bool IsThought)[] parts) : ILlmProvider
    {
        public string ModelId => "fake-thinking";

        public async IAsyncEnumerable<string> StreamCompletionAsync(
            LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            var reasoning = new System.Text.StringBuilder();
            foreach (var (text, isThought) in parts)
            {
                if (isThought)
                {
                    reasoning.Append(text);
                    request.OnReasoningDelta?.Invoke(text);
                }
                else yield return text;
                await Task.Yield();
            }
            if (reasoning.Length > 0) request.OnReasoning?.Invoke(reasoning.ToString());
        }
    }

    private static ChatService Service(IRetriever retriever, ILlmProvider llm) =>
        new(retriever, new NoOpAugmenter(), llm, Options.Create(new RetrievalOptions()),
            new FakeEmbeddingProvider(), Options.Create(new DocumentsOptions { Enabled = false }));

    private static async Task<List<ChatEvent>> Drain(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events) list.Add(e);
        return list;
    }

    [Fact] // Etapy retrievalu MUSZA dotrzec, i to PRZED zrodlami — inaczej UI nie ma czym wypelnic czekania.
    public async Task Retrieval_stages_arrive_before_sources()
    {
        var events = await Drain(Service(
                new StageReportingRetriever(0.9, "embed", "dense", "sparse", "rerank.main"),
                new ThinkingLlm([("Odpowiedź [1].", false)]))
            .AskAsync("czy ponoszę odpowiedzialność?", [], null, default));

        var stages = events.OfType<StageEvent>().Select(s => s.Stage).ToList();
        Assert.Equal(["embed", "dense", "sparse", "rerank.main"], stages.Take(4));

        var firstSources = events.FindIndex(e => e is SourcesEvent);
        var lastRetrievalStage = events.FindLastIndex(e => e is StageEvent { Stage: "rerank.main" });
        Assert.True(lastRetrievalStage < firstSources);

        // Liczby i etykiety docieraja nietkniete (UI je pokazuje).
        var dense = events.OfType<StageEvent>().Single(s => s.Stage == "dense");
        Assert.Equal("etykieta dense", dense.Label);
        Assert.Equal(42, dense.Count);
    }

    [Fact] // Sciezka ODMOWY tez musi pokazywac prace — inaczej uzytkownik czeka 85 s w ciszy po nic.
    public async Task Abstain_path_still_reports_stages()
    {
        var events = await Drain(Service(
                new StageReportingRetriever(0.1, "embed", "dense"), // 0.1 < prog 0.55 => odmowa
                new ThinkingLlm([("nieuzywane", false)]))
            .AskAsync("pytanie bez pokrycia", [], null, default));

        Assert.Contains(events, e => e is StageEvent { Stage: "embed" });
        Assert.Contains(events, e => e is StageEvent { Stage: "dense" });
        Assert.Contains(events, e => e is AbstainEvent);
        Assert.DoesNotContain(events, e => e is TokenEvent); // nie generujemy
    }

    [Fact] // Delty rozumowania przeplataja sie z tokenami i NIC nie ginie (kanal wydrenowany do konca).
    public async Task Reasoning_deltas_interleave_with_tokens_and_nothing_is_lost()
    {
        var events = await Drain(Service(
                new StageReportingRetriever(0.9, "embed"),
                new ThinkingLlm([
                    ("Sprawdzam art. ", true),
                    ("415 KC.", true),
                    ("Ponosisz odpowiedzialność [1].", false),
                    (" Dodatkowo…", false),
                ]))
            .AskAsync("czy ponoszę odpowiedzialność?", [], null, default));

        var deltas = events.OfType<ReasoningDeltaEvent>().Select(d => d.Text).ToList();
        Assert.Equal(["Sprawdzam art. ", "415 KC."], deltas);

        // Rozumowanie POPRZEDZA widoczny tekst — model najpierw myśli, więc UI musi to tak pokazać.
        var lastDelta = events.FindLastIndex(e => e is ReasoningDeltaEvent);
        var firstToken = events.FindIndex(e => e is TokenEvent);
        Assert.True(lastDelta < firstToken);

        // Całość rozumowania nadal przychodzi RAZ na końcu (zapis do historii bez zmian).
        var whole = Assert.IsType<ReasoningEvent>(events.Last(e => e is ReasoningEvent));
        Assert.Equal(string.Concat(deltas), whole.Text);

        // DoneEvent jest OSTATNI — po nim nic nie może wypaść z kanału.
        Assert.IsType<DoneEvent>(events[^1]);
    }

    [Fact] // Model, ktory myslal PO ostatnim widocznym tokenie - ogon kanalu nie moze zginac.
    public async Task Reasoning_after_last_visible_token_is_not_lost()
    {
        var events = await Drain(Service(
                new StageReportingRetriever(0.9, "embed"),
                new ThinkingLlm([
                    ("Odpowiedź [1].", false),
                    ("domyślam sprawdzenie", true), // delta PO ostatnim tokenie
                ]))
            .AskAsync("pytanie", [], null, default));

        Assert.Contains(events, e => e is ReasoningDeltaEvent { Text: "domyślam sprawdzenie" });
        Assert.IsType<DoneEvent>(events[^1]);
    }

    [Fact] // Retriever bez raportowania (dzisiejszy stan) => zero StageEventow z retrievalu, ale reszta bez zmian.
    public async Task Retriever_without_stages_still_answers()
    {
        var events = await Drain(Service(
                new StageReportingRetriever(0.9), // zero etapów
                new ThinkingLlm([("Odpowiedź [1].", false)]))
            .AskAsync("pytanie", [], null, default));

        Assert.Contains(events, e => e is SourcesEvent);
        Assert.Contains(events, e => e is TokenEvent);
        Assert.IsType<DoneEvent>(events[^1]);
        // ChatService dokłada własne etapy (augment, llm) — retrieval nie zgłosił żadnego.
        Assert.DoesNotContain(events.OfType<StageEvent>(), s => s.Stage is "dense" or "embed");
    }
}
