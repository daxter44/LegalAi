using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using PrawoRAG.Api.Services;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Tests.Fakes;

namespace PrawoRAG.Tests.Chat;

/// <summary>
/// T-CONTENT-RETRY (Zadanie 13 planu ROU) — wyzwalacz TREŚCIOWY pętli domykającej + WSPÓLNY BUDŻET
/// naprawczy tury.
///
/// Dlaczego ten wyzwalacz jest WAŻNIEJSZY od progowego: bramka abstynencji patrzy na sygnał
/// retrievalu, ale odmowa z reguły 3 promptu żyje w TREŚCI odpowiedzi — model dostał źródła ponad
/// progiem i sam orzekł, że nie odpowiadają na pytanie. W tym projekcie to norma, nie wyjątek
/// („odmowy są treściowe, nie progowe"), więc bez tego wyzwalacza pętla domykająca w ogóle by tych
/// przypadków nie widziała.
///
/// Drugi temat tego pliku to budżet: regeneracja bramki + druga runda retrievalu + regeneracja po
/// odmowie treściowej to trzy dodatkowe wywołania modelu. Bez wspólnego licznika tura liczyłaby
/// się w minutach.
/// </summary>
public class ContentRefusalRetryTests
{
    private static RetrievedChunk Chunk(string text) => new()
    {
        ChunkId = Guid.CreateVersion7(), DocumentId = Guid.CreateVersion7(), Text = text,
        Source = "ELI", DocType = DocTypes.Act, Title = "Ustawa", Score = 1.0, Similarity = 0.9,
    };

    private sealed class CountingRetriever(params RetrievalResult[] results) : IRetriever
    {
        public int Calls { get; private set; }

        public Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, CancellationToken ct)
        {
            var index = Math.Min(Calls++, results.Length - 1);
            return Task.FromResult(results[index]);
        }
    }

    private sealed class NoOpAugmenter : ITemporalAugmenter
    {
        public Task<IReadOnlyList<RetrievedChunk>> AugmentAsync(
            RetrievalQuery query, IReadOnlyList<RetrievedChunk> retrieved, CancellationToken ct)
            => Task.FromResult(retrieved);
    }

    /// <summary>Augmenter liczący wywołania i doklejający rozpoznawalny chunk-nowelę — dowód, że
    /// ścieżka faktycznie przeszła przez augmenter (diagnoza 2026-09-01: druga runda go pomijała).</summary>
    private sealed class MarkingAugmenter : ITemporalAugmenter
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<RetrievedChunk>> AugmentAsync(
            RetrievalQuery query, IReadOnlyList<RetrievedChunk> retrieved, CancellationToken ct)
        {
            Calls++;
            IReadOnlyList<RetrievedChunk> result =
                [.. retrieved, Chunk($"[NOWELIZACJA-TEST-{Calls}] dołożona przez augmenter")];
            return Task.FromResult(result);
        }
    }

    private sealed class SequenceLlm(params string[] answers) : ILlmProvider
    {
        private int _call;
        public int Calls => _call;
        public string ModelId => "fake";

        public async IAsyncEnumerable<string> StreamCompletionAsync(
            LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return answers[Math.Min(_call++, answers.Length - 1)];
            await Task.CompletedTask;
        }
    }

    private sealed class CountingReformulator(string? result) : IQueryReformulator
    {
        public int Calls { get; private set; }

        public Task<string?> ReformulateAsync(string question, IReadOnlyList<ChatTurn> history, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private static ChatService Service(
        IRetriever retriever, ILlmProvider llm, IQueryReformulator? reformulator,
        bool gapClosing = true, int maxExtraRounds = 1, ITemporalAugmenter? augmenter = null) =>
        new(retriever, augmenter ?? new NoOpAugmenter(), llm,
            Options.Create(new RetrievalOptions
            {
                GapClosingEnabled = gapClosing,
                MaxExtraRounds = maxExtraRounds,
            }),
            new FakeEmbeddingProvider(), Options.Create(new DocumentsOptions { Enabled = false }),
            router: null, Options.Create(new GroundingOptions()), reformulator);

    private static async Task<List<ChatEvent>> Drain(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events) list.Add(e);
        return list;
    }

    /// <summary>Wynik z pokryciem — bramka progowa go przepuszcza, więc odmowa może przyjść
    /// wyłącznie z TREŚCI odpowiedzi.</summary>
    private static RetrievalResult Covered(string text) => new([Chunk(text)], 0.9);

    [Fact] // Diagnoza 2026-09-01: druga runda POMIJALA TemporalAugmenter — nowele wracaly bez markera
           // [NOWELIZACJA] dokladnie tam, gdzie pierwsza generacja juz polegla. Augmenter ma byc
           // wolany w OBU rundach, a jego chunki maja wejsc do zrodel drugiej generacji.
    public async Task Second_round_passes_through_temporal_augmenter()
    {
        var retriever = new CountingRetriever(
            Covered("nietrafiony przepis"),
            Covered("art. 5 w dwoch wersjach"));
        var llm = new SequenceLlm(AbstentionPolicy.Message, "Limit wynosi 225% kwartalnie [1].");
        var augmenter = new MarkingAugmenter();

        var events = await Drain(Service(retriever, llm, new CountingReformulator("limit przychodu"),
                augmenter: augmenter)
            .AskAsync("Do jakich obrotów…?", [], null, default));

        Assert.Equal(2, augmenter.Calls); // runda 1 + runda 2 (lustro, nie tylko pierwsza)
        var lastSources = events.OfType<SourcesEvent>().Last();
        Assert.Contains(lastSources.Sources, s => s.Snippet.Contains("NOWELIZACJA-TEST-2"));
    }

    [Fact] // Odmowa TRESCIOWA => przeformulowanie, druga runda, druga generacja na NOWYCH zrodlach.
    public async Task Content_refusal_triggers_second_round()
    {
        var retriever = new CountingRetriever(
            Covered("nietrafiony przepis"),
            Covered("Prezes UODO — zgłoszenie naruszenia"));
        var llm = new SequenceLlm(AbstentionPolicy.Message, "Zgłaszasz Prezesowi UODO [1].");
        var reformulator = new CountingReformulator("zgłoszenie naruszenia Prezesowi UODO");

        var events = await Drain(Service(retriever, llm, reformulator)
            .AskAsync("komu zgłosić wyciek?", [], null, default));

        Assert.Equal(1, reformulator.Calls);
        Assert.Equal(2, retriever.Calls);
        Assert.Equal(2, llm.Calls);
        Assert.Contains(events, e => e is RetryingRetrievalEvent);
        // Drugie źródła pokazane użytkownikowi — inaczej panel nie zgadzałby się z odpowiedzią.
        Assert.Equal(2, events.OfType<SourcesEvent>().Count());
        Assert.DoesNotContain(AbstentionPolicy.Message,
            string.Concat(events.OfType<TokenEvent>().Select(t => t.Text))[^30..]);
    }

    [Fact] // REGRESJA (fix 2026-08-31): model pisze TYLKO krotka fraze z reguly 3 promptu, BEZ
           // doklejki "Zawez pytanie..." z AbstentionPolicy.Message. Wyzwalacz porownujacy z pelnym
           // Message nigdy jej nie trafial - byl martwy na WSZYSTKICH realnych odmowach (potwierdzone
           // trzema diagnozami produkcyjnymi). Ten test zasiewa fraze dokladnie tak, jak pisze ja model.
    public async Task Real_model_refusal_phrase_triggers_second_round()
    {
        var retriever = new CountingRetriever(
            Covered("nietrafiony przepis"),
            Covered("art. 50 ust. 2 — oznaczanie treści generowanych"));
        var llm = new SequenceLlm(
            "Nie znalazłem jednoznacznej podstawy prawnej dla tego pytania.", // fraza reguły 3 — bez doklejki
            "Tak, art. 50 ust. 2 wymaga oznaczania [1].");
        var reformulator = new CountingReformulator("oznaczanie treści wygenerowanej przez AI");

        var events = await Drain(Service(retriever, llm, reformulator)
            .AskAsync("czy muszę oznaczać tekst znakiem wodnym?", [], null, default));

        Assert.Equal(1, reformulator.Calls);
        Assert.Equal(2, retriever.Calls);
        Assert.Equal(2, llm.Calls);
        Assert.Contains(events, e => e is RetryingRetrievalEvent);
    }

    [Fact] // WARIANT A telemetrii (2026-08-31): odmowa tresciowa, ktora WYSZLA do uzytkownika
           // (reformulator null => bez drugiej rundy), konczy sie DoneEvent(Abstained=true) -
           // metryka nadrzedna (odsetek odmow) liczy sie z tej flagi, a odmowy sa u nas
           // tresciowe, nie progowe (prog 0.0 od znaleziska o sygnale rerankera).
    public async Task Content_refusal_reaching_user_is_marked_abstained()
    {
        var retriever = new CountingRetriever(Covered("x"));
        var llm = new SequenceLlm("Nie znalazłem jednoznacznej podstawy prawnej dla tego pytania.");

        var events = await Drain(Service(retriever, llm, new CountingReformulator(null))
            .AskAsync("pytanie", [], null, default));

        var done = Assert.IsType<DoneEvent>(events[^1]);
        Assert.True(done.Abstained);
    }

    [Fact] // Odpowiedz merytoryczna => Abstained=false (wariant A nie zmienia szczesliwej sciezki).
    public async Task Substantive_answer_is_not_marked_abstained()
    {
        var retriever = new CountingRetriever(Covered("art. 415"));
        var llm = new SequenceLlm("Ponosisz odpowiedzialność [1].");

        var events = await Drain(Service(retriever, llm, new CountingReformulator(null))
            .AskAsync("pytanie", [], null, default));

        var done = Assert.IsType<DoneEvent>(events[^1]);
        Assert.False(done.Abstained);
    }

    [Fact] // Odpowiedz BEZ frazy odmowy => zero przeformulowania i zero dodatkowej rundy.
    public async Task Normal_answer_does_not_trigger_retry()
    {
        var retriever = new CountingRetriever(Covered("art. 415"));
        var llm = new SequenceLlm("Ponosisz odpowiedzialność [1].");
        var reformulator = new CountingReformulator("inne");

        await Drain(Service(retriever, llm, reformulator).AskAsync("pytanie", [], null, default));

        Assert.Equal(0, reformulator.Calls);
        Assert.Equal(1, retriever.Calls);
        Assert.Equal(1, llm.Calls);
    }

    [Fact] // BUDZET: gdy GapClosingRetrieval juz zuzyl dodatkowa runde (odmowa PROGOWA), wyzwalacz
           // tresciowy NIE odpala kolejnej. Inaczej tura mialaby trzy rundy retrievalu.
    public async Task Budget_shared_with_threshold_trigger()
    {
        var retriever = new CountingRetriever(
            new RetrievalResult([Chunk("słaby")], 0.20),   // runda 1: pod progiem → GapClosing działa
            Covered("po przeformułowaniu"));               // runda 2: pokrycie jest
        var llm = new SequenceLlm(AbstentionPolicy.Message); // ale model i tak odmawia treściowo
        var reformulator = new CountingReformulator("inne zapytanie");

        var events = await Drain(Service(retriever, llm, reformulator)
            .AskAsync("pytanie", [], null, default));

        Assert.Equal(2, retriever.Calls);      // DOKŁADNIE dwie rundy, nie trzy
        Assert.Equal(1, reformulator.Calls);   // przeformułowanie tylko raz
        Assert.Equal(1, llm.Calls);            // i jedna generacja
        Assert.Contains(events, e => e is RetryingRetrievalEvent);
    }

    [Fact] // Reformulator zwrocil null => dzisiejsze zachowanie (odmowa tresciowa wychodzi jak dotad).
    public async Task Null_reformulation_keeps_content_refusal()
    {
        var retriever = new CountingRetriever(Covered("x"));
        var llm = new SequenceLlm(AbstentionPolicy.Message);
        var reformulator = new CountingReformulator(null);

        var events = await Drain(Service(retriever, llm, reformulator)
            .AskAsync("pytanie", [], null, default));

        Assert.Equal(1, retriever.Calls);
        Assert.Equal(1, llm.Calls);
        Assert.DoesNotContain(events, e => e is RetryingRetrievalEvent);
        Assert.Contains(AbstentionPolicy.Message,
            string.Concat(events.OfType<TokenEvent>().Select(t => t.Text)));
    }

    [Fact] // Druga runda BEZ pokrycia => nie podmieniamy kontekstu na gorszy; odmowa zostaje.
    public async Task Second_round_without_coverage_keeps_first_context()
    {
        var retriever = new CountingRetriever(
            Covered("pierwszy kontekst"),
            new RetrievalResult([Chunk("gorszy")], 0.10)); // pod progiem
        var llm = new SequenceLlm(AbstentionPolicy.Message);
        var reformulator = new CountingReformulator("inne");

        var events = await Drain(Service(retriever, llm, reformulator)
            .AskAsync("pytanie", [], null, default));

        Assert.Equal(2, retriever.Calls);
        Assert.Equal(1, llm.Calls);                                   // druga generacja nie ma sensu
        Assert.Single(events.OfType<SourcesEvent>());                  // panel źródeł bez zmian
    }

    [Fact] // Flaga GapClosingEnabled=false => wyzwalacz tresciowy nieaktywny (wylacznik dziala na oba).
    public async Task Flag_off_disables_content_trigger()
    {
        var retriever = new CountingRetriever(Covered("x"));
        var llm = new SequenceLlm(AbstentionPolicy.Message);
        var reformulator = new CountingReformulator("inne");

        await Drain(Service(retriever, llm, reformulator, gapClosing: false)
            .AskAsync("pytanie", [], null, default));

        Assert.Equal(0, reformulator.Calls);
        Assert.Equal(1, retriever.Calls);
    }

    [Fact] // MaxExtraRounds=0 => tak samo, bez dodatkowej rundy.
    public async Task Zero_extra_rounds_disables_content_trigger()
    {
        var retriever = new CountingRetriever(Covered("x"));
        var llm = new SequenceLlm(AbstentionPolicy.Message);
        var reformulator = new CountingReformulator("inne");

        await Drain(Service(retriever, llm, reformulator, maxExtraRounds: 0)
            .AskAsync("pytanie", [], null, default));

        Assert.Equal(0, reformulator.Calls);
        Assert.Equal(1, retriever.Calls);
    }

    [Fact] // Brak reformulatora w kontenerze => zero zmian wzgledem dzisiejszego zachowania.
    public async Task Missing_reformulator_keeps_today_behaviour()
    {
        var retriever = new CountingRetriever(Covered("x"));
        var llm = new SequenceLlm(AbstentionPolicy.Message);

        var events = await Drain(Service(retriever, llm, reformulator: null)
            .AskAsync("pytanie", [], null, default));

        Assert.Equal(1, retriever.Calls);
        Assert.DoesNotContain(events, e => e is RetryingRetrievalEvent);
    }
}
