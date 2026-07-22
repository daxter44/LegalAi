using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrawoRAG.Api.Services;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Embeddings;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Tests.Fakes;

namespace PrawoRAG.Tests.Analysis;

/// <summary>
/// T-SPK-3 — orkiestrator map-reduce (fakes, bez DB/LLM/sieci): wyniki per jednostka w kolejności
/// dokumentu, werdykty z pierwszej linii, limit równoległości respektowany, awaria/odmowa jednej
/// jednostki nie wali sesji, streszczenie jako osobne wywołanie LLM bez retrievalu, limity CostGuard
/// liczą każdą jednostkę.
/// </summary>
public class AnalysisRunnerTests
{
    private sealed class FixedRetriever(double signal) : IRetriever
    {
        public Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, CancellationToken ct) =>
            Task.FromResult(new RetrievalResult([new RetrievedChunk
            {
                ChunkId = Guid.NewGuid(), DocumentId = Guid.NewGuid(),
                Text = "Art. 484. Kara umowna…", Source = "ELI", DocType = DocTypes.Act,
                Title = "Kodeks cywilny", Score = 1.0,
            }], signal));
    }

    private sealed class NoOpAugmenter : ITemporalAugmenter
    {
        public Task<IReadOnlyList<RetrievedChunk>> AugmentAsync(
            RetrievalQuery query, IReadOnlyList<RetrievedChunk> retrieved, CancellationToken ct)
            => Task.FromResult(retrieved);
    }

    /// <summary>LLM sterowany funkcją odpowiedzi; mierzy maksymalną RÓWNOCZESNOŚĆ wywołań
    /// (weryfikacja semafora) i zbiera żądania (rozróżnienie map vs streszczenie).</summary>
    private sealed class ScriptedLlm(Func<LlmRequest, string> answer) : ILlmProvider
    {
        private int _current;
        private int _maxConcurrent;
        public List<LlmRequest> Requests { get; } = [];
        public int MaxConcurrent => _maxConcurrent;
        public string ModelId => "fake";

        public async IAsyncEnumerable<string> StreamCompletionAsync(
            LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            var cur = Interlocked.Increment(ref _current);
            int seen;
            while (cur > (seen = Volatile.Read(ref _maxConcurrent)))
                Interlocked.CompareExchange(ref _maxConcurrent, cur, seen);
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref _current);
            yield return answer(request);
        }
    }

    private static IReadOnlyList<DocUnit> Units(int n) =>
        Enumerable.Range(1, n).Select(i => new DocUnit(i, $"§ {i}", $"§ {i} Treść postanowienia numer {i}.")).ToList();

    private static (AnalysisRunner Runner, AnalysisSessionStore Store) Harness(
        ILlmProvider llm, double signal = 0.9, int maxParallelism = 4, AccessOptions? access = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingProvider>(new FakeEmbeddingProvider());
        services.AddSingleton(llm);
        services.AddScoped<IChatService>(sp => new ChatService(
            new FixedRetriever(signal), new NoOpAugmenter(), sp.GetRequiredService<ILlmProvider>(),
            Options.Create(new RetrievalOptions()), sp.GetRequiredService<IEmbeddingProvider>(),
            Options.Create(new DocumentsOptions())));
        var provider = services.BuildServiceProvider();

        var options = Options.Create(new AnalysisOptions { MaxParallelism = maxParallelism });
        var costGuard = new CostGuard(Options.Create(access ?? new AccessOptions()), TimeProvider.System);
        return (new AnalysisRunner(provider.GetRequiredService<IServiceScopeFactory>(), options, costGuard),
                new AnalysisSessionStore(TimeProvider.System, options));
    }

    private static async Task<AnalysisSnapshot> RunAsync(
        AnalysisRunner runner, AnalysisSessionStore store, int units, string prompt = "oceń ryzyka")
    {
        var session = store.Create("umowa.pdf", 2, prompt, Units(units), unitsTruncated: false);
        await runner.RunAsync(session, "tester", default);
        return session.Snapshot();
    }

    [Fact]
    public async Task Happy_path_all_units_analyzed_with_summary()
    {
        var llm = new ScriptedLlm(r => r.Messages[0].Content == AnalysisPrompts.SummarySystemPrompt
            ? "Umowa zawiera jedno ryzyko."
            : "WERDYKT: OK\nPostanowienie zgodne z prawem [1].");
        var (runner, store) = Harness(llm);

        var snap = await RunAsync(runner, store, 3);

        Assert.Equal(AnalysisStatus.Done, snap.Status);
        Assert.Equal(3, snap.Completed);
        Assert.All(snap.Results, r =>
        {
            Assert.Equal(UnitVerdict.Ok, r!.Verdict);
            Assert.Equal("Postanowienie zgodne z prawem [1].", r.Answer); // linia werdyktu zdjęta
            Assert.NotEmpty(r.Sources);                                   // cytaty przeniesione strukturalnie
        });
        Assert.Equal("Umowa zawiera jedno ryzyko.", snap.Summary);
        Assert.Equal(4, llm.Requests.Count); // 3 × map + 1 × streszczenie
        Assert.Single(llm.Requests, r => r.Messages[0].Content == AnalysisPrompts.SummarySystemPrompt);
    }

    [Fact] // map-prompt niesie intencję użytkownika + treść jednostki; streszczenie NIE ma źródeł (bez retrievalu)
    public async Task Map_prompt_carries_intent_and_unit_text()
    {
        var llm = new ScriptedLlm(r => r.Messages[0].Content == AnalysisPrompts.SummarySystemPrompt
            ? "Streszczenie." : "WERDYKT: OK\nOdpowiedź [1].");
        var (runner, store) = Harness(llm);

        await RunAsync(runner, store, 2, prompt: "oceń ryzyka najemcy");

        var map = llm.Requests.First(r => r.Messages[0].Content != AnalysisPrompts.SummarySystemPrompt);
        Assert.Contains("oceń ryzyka najemcy", map.Messages[^1].Content);
        Assert.Contains("Treść postanowienia numer", map.Messages[^1].Content);
        Assert.Contains("WERDYKT", map.Messages[^1].Content);

        var summary = llm.Requests.Single(r => r.Messages[0].Content == AnalysisPrompts.SummarySystemPrompt);
        Assert.DoesNotContain("ŹRÓDŁA:", summary.Messages[^1].Content);
        Assert.Contains("§ 1: OK", summary.Messages[^1].Content); // digest werdyktów
    }

    [Fact]
    public async Task Parallelism_limit_is_respected()
    {
        var llm = new ScriptedLlm(_ => "WERDYKT: OK\nOdpowiedź [1].");
        var (runner, store) = Harness(llm, maxParallelism: 1);

        await RunAsync(runner, store, 4);

        Assert.Equal(1, llm.MaxConcurrent);
    }

    [Fact] // słaby sygnał korpusu → wszystkie jednostki BRAK ŹRÓDEŁ, sesja kończy się poprawnie
    public async Task Abstention_yields_no_sources_verdicts()
    {
        var llm = new ScriptedLlm(_ => "Streszczenie.");
        var (runner, store) = Harness(llm, signal: 0.1);

        var snap = await RunAsync(runner, store, 3);

        Assert.Equal(AnalysisStatus.Done, snap.Status);
        Assert.All(snap.Results, r =>
        {
            Assert.Equal(UnitVerdict.NoSources, r!.Verdict);
            Assert.False(string.IsNullOrEmpty(r.Answer)); // komunikat odmowy, nie pusta karta
        });
        Assert.Single(llm.Requests); // map nie woła LLM przy odmowie — tylko streszczenie
    }

    [Fact] // wyjątek LLM przy jednej jednostce → werdykt BŁĄD tej jednostki, reszta OK, sesja Done
    public async Task Single_unit_failure_does_not_fail_session()
    {
        var llm = new ScriptedLlm(r =>
            r.Messages[^1].Content.Contains("§ 2 Treść")
                ? throw new InvalidOperationException("awaria providera")
                : r.Messages[0].Content == AnalysisPrompts.SummarySystemPrompt
                    ? "Streszczenie." : "WERDYKT: OK\nOdpowiedź [1].");
        var (runner, store) = Harness(llm);

        var snap = await RunAsync(runner, store, 3);

        Assert.Equal(AnalysisStatus.Done, snap.Status);
        Assert.Equal(UnitVerdict.Ok, snap.Results[0]!.Verdict);
        Assert.Equal(UnitVerdict.Error, snap.Results[1]!.Verdict);
        Assert.Contains("awaria providera", snap.Results[1]!.Error);
        Assert.Equal(UnitVerdict.Ok, snap.Results[2]!.Verdict);
    }

    [Fact] // twardy dzienny limit zapytań wyczerpany w połowie → jednostki ponad limit = BŁĄD z komunikatem, bez wołania LLM
    public async Task Cost_guard_limits_units()
    {
        var llm = new ScriptedLlm(_ => "WERDYKT: OK\nOdpowiedź [1].");
        var access = new AccessOptions { Enabled = true, MaxUserRequestsPerDay = 2 };
        var (runner, store) = Harness(llm, maxParallelism: 1, access: access);

        var snap = await RunAsync(runner, store, 4);

        Assert.Equal(AnalysisStatus.Done, snap.Status);
        Assert.Equal(2, snap.Results.Count(r => r!.Verdict == UnitVerdict.Ok));
        Assert.Equal(2, snap.Results.Count(r => r!.Verdict == UnitVerdict.Error && r.Error!.Contains("limit")));
        Assert.Equal(2, llm.Requests.Count);   // streszczenie też ucięte limitem
        Assert.Null(snap.Summary);
    }

    [Fact] // embeddingi jednostek trafiają do sesji (routing dopytań)
    public async Task Unit_embeddings_are_stored()
    {
        var llm = new ScriptedLlm(_ => "WERDYKT: OK\nOdpowiedź [1].");
        var (runner, store) = Harness(llm);
        var session = store.Create("u.pdf", 1, "p", Units(3), false);

        await runner.RunAsync(session, "tester", default);

        Assert.Equal(3, session.UnitEmbeddings!.Count);
    }

    [Theory]
    [InlineData("WERDYKT: OK\nWszystko dobrze [1].", UnitVerdict.Ok, "Wszystko dobrze [1].")]
    [InlineData("WERDYKT: RYZYKO\nKlauzula abuzywna [2].", UnitVerdict.Risk, "Klauzula abuzywna [2].")]
    [InlineData("werdykt: brak źródeł\nNie znalazłem.", UnitVerdict.NoSources, "Nie znalazłem.")]
    [InlineData("Zwykła odpowiedź bez werdyktu [1].", UnitVerdict.Unknown, "Zwykła odpowiedź bez werdyktu [1].")]
    public void ParseVerdict_reads_first_line(string full, UnitVerdict verdict, string answer)
    {
        var (v, a) = AnalysisPrompts.ParseVerdict(full);
        Assert.Equal(verdict, v);
        Assert.Equal(answer, a);
    }

    [Fact] // fraza odmowy (reguła 3) ma pierwszeństwo przed werdyktem z pierwszej linii
    public void ParseVerdict_refusal_marker_wins()
    {
        var (v, _) = AnalysisPrompts.ParseVerdict(
            "WERDYKT: OK\nNie mam wystarczających źródeł, aby odpowiedzieć.");
        Assert.Equal(UnitVerdict.NoSources, v);
    }
}
