using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using PrawoRAG.Api.Services;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Tests.Fakes;

namespace PrawoRAG.Tests.Chat;

/// <summary>
/// T-ROUTER-CHAT (Zadanie 8 planu ROU) — wpięcie routera w ChatService.
///
/// To najbardziej wrażliwe testy w całym planie: router jest JEDYNYM mechanizmem, który może
/// sprawić, że odpowiedź powstanie bez źródeł. Dlatego pilnujemy tu wszystkich trzech linii obrony
/// (flaga, forceRetrieval, bezpiecznik) osobno, a nie tylko szczęśliwej ścieżki.
/// </summary>
public class ChatServiceRouterTests
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

    /// <summary>Router, który zawsze orzeka to samo — pozwala testować SKUTKI orzeczenia,
    /// niezależnie od jakości modelu.</summary>
    private sealed class StubRouter(bool needsLaw) : IIntentRouter
    {
        public int Calls { get; private set; }

        public Task<RouteDecision> RouteAsync(
            string question, IReadOnlyList<ChatTurn> history, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new RouteDecision(needsLaw, null, needsLaw ? "prawne" : "powitanie"));
        }
    }

    private static ChatService Service(
        IRetriever retriever, ILlmProvider llm, IIntentRouter? router, bool routerEnabled) =>
        new(retriever, new NoOpAugmenter(), llm,
            Options.Create(new RetrievalOptions { RouterEnabled = routerEnabled }),
            new FakeEmbeddingProvider(), Options.Create(new DocumentsOptions { Enabled = false }), router);

    private static async Task<List<ChatEvent>> Drain(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events) list.Add(e);
        return list;
    }

    [Fact] // FLAGA WYLACZONA = dzisiejsze zachowanie bajt w bajt: router niewolany, retrieval zawsze.
    public async Task Flag_off_never_calls_router()
    {
        var retriever = new CountingRetriever(0.9);
        var router = new StubRouter(needsLaw: false); // gdyby zapytać — powiedziałby „pomiń bazę"

        var events = await Drain(Service(retriever, new FakeLlm("Odpowiedź [1]."), router, routerEnabled: false)
            .AskAsync("siema", [], null, default));

        Assert.Equal(0, router.Calls);
        Assert.Equal(1, retriever.Calls);
        Assert.Contains(events, e => e is SourcesEvent);
        Assert.DoesNotContain(events, e => e is NoRetrievalEvent);
    }

    [Fact] // Small-talk przy wlaczonej fladze: ZERO wywolan retrievalu, jawne oznaczenie w UI.
    public async Task Smalltalk_skips_retrieval_entirely()
    {
        var retriever = new CountingRetriever(0.9);
        var llm = new FakeLlm("Cześć! W czym mogę pomóc?");

        var events = await Drain(Service(retriever, llm, new StubRouter(needsLaw: false), routerEnabled: true)
            .AskAsync("siema", [], null, default));

        Assert.Equal(0, retriever.Calls);                                  // baza nietknięta
        Assert.Contains(events, e => e is NoRetrievalEvent);               // jawne oznaczenie
        Assert.DoesNotContain(events, e => e is SourcesEvent);             // brak źródeł
        Assert.DoesNotContain(events, e => e is AbstainEvent);             // bramka nie dotyczy
        Assert.Contains(events, e => e is TokenEvent);                     // odpowiedź jest
        var done = Assert.IsType<DoneEvent>(events[^1]);
        Assert.Null(done.Check);                                           // nie ma czego walidować
        Assert.False(done.Abstained);
    }

    [Fact] // Sciezka small-talku uzywa WLASNEGO promptu, nie GroundedPrompt (zero regul cytowania [n]).
    public async Task Smalltalk_uses_its_own_prompt()
    {
        var llm = new FakeLlm("Cześć!");
        await Drain(Service(new CountingRetriever(0.9), llm, new StubRouter(false), routerEnabled: true)
            .AskAsync("siema", [], null, default));

        var system = llm.LastRequest!.Messages.Single(m => m.Role == ChatRole.System).Content;
        Assert.Contains("NIE jest pytaniem prawnym", system);
        Assert.DoesNotContain("[1], [2]", system);   // reguły cytowania nie mają tu czego cytować
        Assert.Equal(256, llm.LastRequest.MaxTokens); // reguła R2: bez rozumowania na „siema"
    }

    [Fact] // Router mowi "prawne" => normalna sciezka z retrievalem i zrodlami.
    public async Task Needs_law_goes_through_retrieval()
    {
        var retriever = new CountingRetriever(0.9);
        var events = await Drain(Service(retriever, new FakeLlm("Odpowiedź [1]."),
                new StubRouter(needsLaw: true), routerEnabled: true)
            .AskAsync("czy ponoszę odpowiedzialność?", [], null, default));

        Assert.Equal(1, retriever.Calls);
        Assert.Contains(events, e => e is SourcesEvent);
        Assert.DoesNotContain(events, e => e is NoRetrievalEvent);
    }

    [Fact] // BEZPIECZNIK: token prawny w wiadomosci => retrieval WYMUSZONY, router nawet niewolany.
    public async Task Legal_token_forces_retrieval_without_asking_router()
    {
        var retriever = new CountingRetriever(0.9);
        var router = new StubRouter(needsLaw: false); // router byłby w błędzie

        await Drain(Service(retriever, new FakeLlm("Odpowiedź [1]."), router, routerEnabled: true)
            .AskAsync("co z art. 5?", [], null, default));

        Assert.Equal(0, router.Calls);      // oszczędzone wywołanie modelu
        Assert.Equal(1, retriever.Calls);   // i tak idziemy do bazy
    }

    [Fact] // forceRetrieval (ANALIZA PISM) omija router calkowicie - jednostka dokumentu bez tokenu
           // prawnego nie moze trafic na sciezke bez zrodel.
    public async Task Force_retrieval_bypasses_router()
    {
        var retriever = new CountingRetriever(0.9);
        var router = new StubRouter(needsLaw: false);

        await Drain(Service(retriever, new FakeLlm("Odpowiedź [1]."), router, routerEnabled: true)
            .AskAsync("Preambuła. Strony zawierają niniejszą umowę.", [], null,
                forceRetrieval: true, default));

        Assert.Equal(0, router.Calls);
        Assert.Equal(1, retriever.Calls);
    }

    [Fact] // Brak zarejestrowanego routera (null) => retrieval, bez wyjatku.
    public async Task Missing_router_falls_back_to_retrieval()
    {
        var retriever = new CountingRetriever(0.9);
        await Drain(Service(retriever, new FakeLlm("Odpowiedź [1]."), router: null, routerEnabled: true)
            .AskAsync("siema", [], null, default));

        Assert.Equal(1, retriever.Calls);
    }

    // --- KONTROLA NEGATYWNA (obowiązkowa, Zadanie 8 planu) ---
    // Reprezentatywne pytania prawne, w tym te NAJBARDZIEJ podatne na fałszywy small-talk: krótkie,
    // potoczne, z powitaniem. Router jest tu ustawiony na „pomiń bazę" — czyli sprawdzamy, czy
    // pozostałe linie obrony wystarczą. Jeżeli któreś przejdzie na ścieżkę bez źródeł, to jest
    // dokładnie ten błąd, który plan nazywa warunkiem zabicia fazy.

    [Theory]
    [InlineData("co z art. 5?")]
    [InlineData("a co z § 2?")]
    [InlineData("Dz.U. 2025 poz. 1815 — co wprowadza?")]
    [InlineData("III SA/Po 154/26")]
    [InlineData("dzięki, a co z terminem z art. 300?")]
    [InlineData("czy KC reguluje karę umowną?")]
    [InlineData("ustawa o ochronie danych osobowych — kto jest administratorem?")]
    [InlineData("ordynacja podatkowa a przedawnienie")]
    public async Task Legal_questions_never_reach_smalltalk_path_even_if_router_is_wrong(string question)
    {
        var retriever = new CountingRetriever(0.9);

        var events = await Drain(Service(retriever, new FakeLlm("Odpowiedź [1]."),
                new StubRouter(needsLaw: false), routerEnabled: true)
            .AskAsync(question, [], null, default));

        Assert.Equal(1, retriever.Calls);
        Assert.DoesNotContain(events, e => e is NoRetrievalEvent);
    }
}
