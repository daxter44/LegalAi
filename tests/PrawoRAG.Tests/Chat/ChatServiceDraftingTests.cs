using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using PrawoRAG.Api.Services;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Tests.Fakes;

namespace PrawoRAG.Tests.Chat;

/// <summary>
/// Horyzont 0 draftingu w ChatService (rozmowa 2026-08-28): prośba o sporządzenie dokumentu
/// (1) omija router i WYMUSZA retrieval — wymogi pisma trzeba znaleźć w przepisach,
/// (2) dokleja DraftingRules do promptu ugruntowanego,
/// (3) jest widoczna jako etap w UI.
/// Zwykłe pytania: zero zmian (prompt bajt w bajt, router działa jak dotąd).
/// </summary>
public class ChatServiceDraftingTests
{
    private sealed class CountingRetriever(double similarity) : IRetriever
    {
        public int Calls { get; private set; }

        public Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, CancellationToken ct)
        {
            Calls++;
            var chunk = new RetrievedChunk
            {
                ChunkId = Guid.CreateVersion7(), DocumentId = Guid.CreateVersion7(),
                Text = "Art. 455 KC. Jeżeli termin spełnienia świadczenia nie jest oznaczony…",
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

    /// <summary>Router-wartownik: każde wywołanie w teście draftingu = złamany bezpiecznik (4).</summary>
    private sealed class ThrowingRouter : IIntentRouter
    {
        public Task<RouteDecision> RouteAsync(string question, IReadOnlyList<ChatTurn> history, CancellationToken ct)
            => throw new InvalidOperationException("Router nie może być wołany dla prośby o dokument.");
    }

    private static ChatService Service(IRetriever retriever, ILlmProvider llm, IIntentRouter? router) =>
        new(retriever, new NoOpAugmenter(), llm,
            Options.Create(new RetrievalOptions { RouterEnabled = true }),
            new FakeEmbeddingProvider(), Options.Create(new DocumentsOptions { Enabled = false }), router);

    private static async Task<List<ChatEvent>> Drain(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Prosba_o_dokument_omija_router_wymusza_retrieval_i_dokleja_reguly()
    {
        var retriever = new CountingRetriever(0.9);
        var llm = new FakeLlm("Nie przygotowuję pism, ale wezwanie do zapłaty musi zawierać… [1]");

        var events = await Drain(Service(retriever, llm, new ThrowingRouter())
            .AskAsync("przygotuj wezwanie do zapłaty za niezapłaconą fakturę", [], null, default));

        Assert.Equal(1, retriever.Calls);                                   // retrieval wymuszony
        Assert.Contains(events, e => e is StageEvent { Stage: "drafting" }); // etap widoczny w UI
        Assert.Contains(events, e => e is SourcesEvent);                    // odpowiedź NA źródłach

        var system = llm.LastRequest!.Messages[0];
        Assert.Equal(ChatRole.System, system.Role);
        Assert.Contains("PROŚBA O DOKUMENT", system.Content);               // doklejka DraftingRules
    }

    [Fact]
    public async Task Zwykle_pytanie_prawne_bez_zmian_w_promptcie()
    {
        var llm = new FakeLlm("Odpowiedź [1].");

        var events = await Drain(Service(new CountingRetriever(0.9), llm, router: null)
            .AskAsync("jakie są terminy przedawnienia roszczeń?", [], null, default));

        Assert.DoesNotContain(events, e => e is StageEvent { Stage: "drafting" });
        Assert.DoesNotContain("PROŚBA O DOKUMENT", llm.LastRequest!.Messages[0].Content);
    }
}
