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

    [Fact] // ODM-1: pusty strumien modelu na sciezce bez retrievalu -> serwer emituje standardowe
           // zdanie odmowy (trafia do zapisu/historii/API), a Done.Abstained zostaje false (ODM-3:
           // odmowa "to nie prawo" nie liczy sie do metryki odmow).
    public async Task Smalltalk_empty_output_gets_out_of_scope_message()
    {
        var events = await Drain(Service(new CountingRetriever(0.9), new FakeLlm("  "),
                new StubRouter(needsLaw: false), routerEnabled: true)
            .AskAsync("Podaj przepis na zupę pomidorową", [], null, default));

        var text = string.Concat(events.OfType<TokenEvent>().Select(t => t.Text));
        Assert.Contains(PrawoRAG.Llm.Grounding.SmalltalkPrompt.OutOfScopeMessage, text);
        Assert.False(Assert.IsType<DoneEvent>(events[^1]).Abstained);
    }

    [Fact] // US-2.11 (AI Act art. 50 ust. 2): oznaczenie pochodzenia PRZED pierwszym tokenem, na OBU
           // sciezkach (grounded i smalltalk) - konsument urywajacy strumien tez musi je dostac.
    public async Task Provenance_precedes_first_token_on_both_paths()
    {
        foreach (var needsLaw in new[] { true, false })
        {
            var events = await Drain(Service(new CountingRetriever(0.9), new FakeLlm("Odpowiedź [1]."),
                    new StubRouter(needsLaw), routerEnabled: true)
                .AskAsync("pytanie", [], null, default));

            var provenanceAt = events.FindIndex(e => e is ProvenanceEvent);
            var firstTokenAt = events.FindIndex(e => e is TokenEvent);
            Assert.True(provenanceAt >= 0, $"brak ProvenanceEvent (needsLaw={needsLaw})");
            Assert.True(provenanceAt < firstTokenAt, "oznaczenie musi iść PRZED pierwszym tokenem");

            var p = Assert.IsType<ProvenanceEvent>(events[provenanceAt]);
            Assert.True(p.AiGenerated);
            Assert.Equal("fake", p.Model);
            Assert.StartsWith("OmniaSI/", p.System);
            Assert.Equal(needsLaw, p.Grounded);
            Assert.Single(events.OfType<ProvenanceEvent>()); // RAZ na turę
        }
    }

    [Fact] // Model odpowiedzial niepusto -> serwer NICZEGO nie dokleja (zdanie zastepcze tylko na pustke).
    public async Task Smalltalk_nonempty_output_is_not_modified()
    {
        var events = await Drain(Service(new CountingRetriever(0.9), new FakeLlm("Cześć!"),
                new StubRouter(needsLaw: false), routerEnabled: true)
            .AskAsync("siema", [], null, default));

        var text = string.Concat(events.OfType<TokenEvent>().Select(t => t.Text));
        Assert.Equal("Cześć!", text);
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
        // 512, nie 256: ta ścieżka obsługuje też „streść poprzednią odpowiedź w punktach", a to
        // potrzebuje miejsca na treść. Reguła R2 (bez rozumowania na tej ścieżce) trzyma się rzędu
        // wielkości — odpowiedź na źródłach ma limit wielokrotnie większy.
        Assert.Equal(512, llm.LastRequest.MaxTokens);
    }

    // --- HISTORIA NA ŚCIEŻCE BEZ RETRIEVALU (fix 2026-08-27) ---
    // Defekt: SmalltalkAsync dostawał SAMO pytanie. Router orzeka „przepisy niepotrzebne" m.in. dla
    // „streść to krócej", więc model trafiał tam bez czegokolwiek do streszczenia — i albo pytał
    // „co mam streścić?", albo dorabiał treść z pamięci. Ta ścieżka nie ma bramki abstynencji ani
    // walidacji cytatów, więc nie miało tego co wyłapać.

    [Fact]
    public async Task Smalltalk_path_receives_conversation_history()
    {
        var llm = new FakeLlm("Krócej: odpowiadasz na zasadach ogólnych.");
        ChatTurn[] history = [new("kto odpowiada za szkodę?", "Odpowiada sprawca [1], na zasadach ogólnych.")];

        await Drain(Service(new CountingRetriever(0.9), llm, new StubRouter(false), routerEnabled: true)
            .AskAsync("streść to krócej", history, null, default));

        var messages = llm.LastRequest!.Messages;
        Assert.Contains(messages, m => m.Role == ChatRole.User && m.Content.Contains("kto odpowiada za szkodę?"));
        Assert.Contains(messages, m => m.Role == ChatRole.Assistant && m.Content.Contains("Odpowiada sprawca"));
        Assert.Equal(ChatRole.User, messages[^1].Role);                      // pytanie bieżące na końcu
        Assert.Contains("streść to krócej", messages[^1].Content);
    }

    [Fact] // Markery [n] z tamtej tury ZDJETE - tu nie ma zadnych zrodel, wiec nie moglyby na nic wskazywac.
    public async Task Smalltalk_history_answer_has_no_citation_markers()
    {
        var llm = new FakeLlm("ok");
        ChatTurn[] history = [new("pytanie", "Teza [1] oraz teza [2].")];

        await Drain(Service(new CountingRetriever(0.9), llm, new StubRouter(false), routerEnabled: true)
            .AskAsync("krócej", history, null, default));

        var assistant = llm.LastRequest!.Messages.Single(m => m.Role == ChatRole.Assistant).Content;
        Assert.DoesNotContain("[1]", assistant);
        Assert.DoesNotContain("[2]", assistant);
    }

    [Fact] // Tura z ABSTYNENCJA (Answer=null) konczy historie rola User - dwie wiadomosci User z rzedu
           // lamia naprzemiennosc, ktorej wymagaja szablony czatu lokalnych modeli.
    public async Task Smalltalk_coalesces_roles_after_abstained_turn()
    {
        var llm = new FakeLlm("ok");
        ChatTurn[] history = [new("pytanie bez odpowiedzi", null)];

        await Drain(Service(new CountingRetriever(0.9), llm, new StubRouter(false), routerEnabled: true)
            .AskAsync("to co teraz?", history, null, default));

        var messages = llm.LastRequest!.Messages;
        for (var i = 1; i < messages.Count; i++)
            Assert.NotEqual(messages[i - 1].Role, messages[i].Role);
        Assert.Contains("pytanie bez odpowiedzi", messages[^1].Content);     // scalone, nie zgubione
        Assert.Contains("to co teraz?", messages[^1].Content);
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
