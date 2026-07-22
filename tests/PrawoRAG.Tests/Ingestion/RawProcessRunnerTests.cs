using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Ingestion;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// Odporność RawProcessRunner (plan ODP; czyste fake'i, bez DB/sieci/plików magazynu):
/// fast-skip omija pipeline całkowicie, bezpiecznik przerywa serię porażek (i zeruje się po
/// każdym innym wyniku, także fast-skipie), raport JSONL niesie pełny błąd z etapem i pozycją.
/// </summary>
public sealed class RawProcessRunnerTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "praworag-odp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_logDir)) Directory.Delete(_logDir, recursive: true);
    }

    private static RawDocument Doc(string id, string content = "treść dokumentu") => new()
    {
        Source = "T", ExternalId = id, DocType = "judgment", RawContent = content,
    };

    private sealed class InMemoryStore(params RawDocument[] docs) : IRawDocumentStore
    {
        public Task<bool> ExistsAsync(string source, string externalId, CancellationToken ct) =>
            Task.FromResult(docs.Any(d => d.Source == source && d.ExternalId == externalId));

        public Task SaveAsync(RawDocument document, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> CountAsync(string source, CancellationToken ct) =>
            Task.FromResult(docs.Count(d => d.Source == source));

        public async IAsyncEnumerable<RawDocument> EnumerateAsync(string source, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var d in docs.Where(d => d.Source == source)) yield return d;
            await Task.CompletedTask;
        }

        public Task<RawDocument?> ReadAsync(string source, string externalId, CancellationToken ct) =>
            Task.FromResult(docs.FirstOrDefault(d => d.Source == source && d.ExternalId == externalId));
    }

    /// <summary>Fake pipeline'u: wynik sterowany skryptem (dokument, nr wywołania); zlicza co przetworzył.</summary>
    private sealed class ScriptedPipeline(Func<RawDocument, int, IngestResult> script) : IIngestionPipeline
    {
        private int _calls;

        public List<string> Processed { get; } = [];

        public Task<IngestResult> ProcessAsync(RawDocument raw, CancellationToken ct)
        {
            Processed.Add(raw.ExternalId);
            return Task.FromResult(script(raw, _calls++));
        }
    }

    private RawProcessRunner Runner(ScriptedPipeline pipeline, IRawDocumentStore store, int failStreakLimit = 10, int parallelism = 1)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IIngestionPipeline>(pipeline);
        var sp = services.BuildServiceProvider();
        var opt = new ProcessOptions { FailStreakLimit = failStreakLimit, FailureLogDir = _logDir, ProcessParallelism = parallelism };
        return new RawProcessRunner(
            sp.GetRequiredService<IServiceScopeFactory>(), store, Options.Create(opt),
            NullLogger<RawProcessRunner>.Instance);
    }

    /// <summary>Pipeline thread-safe do testów równoległości: liczy realną RÓWNOCZESNOŚĆ wywołań
    /// (dowód, że dokumenty idą naraz) i zwraca sukces.</summary>
    private sealed class ConcurrentPipeline : IIngestionPipeline
    {
        private int _current, _maxConcurrent, _calls;
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);
        public int Calls => Volatile.Read(ref _calls);

        public async Task<IngestResult> ProcessAsync(RawDocument raw, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            var cur = Interlocked.Increment(ref _current);
            int seen;
            while (cur > (seen = Volatile.Read(ref _maxConcurrent)))
                Interlocked.CompareExchange(ref _maxConcurrent, cur, seen);
            await Task.Delay(30, ct);
            Interlocked.Decrement(ref _current);
            return new IngestResult(IngestOutcome.Inserted);
        }
    }

    private static IngestResult Ok() => new(IngestOutcome.Inserted);

    private static IngestResult Fail(string stage = "embed") =>
        new(IngestOutcome.Failed, stage, new InvalidOperationException("TEI: connection refused"));

    [Fact] // ODP-1: trafienie w zbiór → pipeline NIE jest wołany (zero scope'ów/DB per skip)
    public async Task FastSkip_hit_bypasses_pipeline_entirely()
    {
        var (a, b) = (Doc("a"), Doc("b"));
        var pipeline = new ScriptedPipeline((_, _) => Ok());
        var skip = ProcessSkipSet.From([("a", Hashing.Sha256Hex(a.RawContent))]);

        var summary = await Runner(pipeline, new InMemoryStore(a, b)).RunAsync("T", null, skip, default);

        Assert.Equal(["b"], pipeline.Processed); // „a" pominięte bez dotykania pipeline'u
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(1, summary.Inserted);
    }

    [Fact] // ODP-1: zmieniona treść (inny hash) → dokument PRZECHODZI do pipeline'u (parytet idempotencji)
    public async Task FastSkip_changed_content_goes_to_pipeline()
    {
        var a = Doc("a", "treść po NOWELIZACJI");
        var pipeline = new ScriptedPipeline((_, _) => new IngestResult(IngestOutcome.Updated));
        var skip = ProcessSkipSet.From([("a", Hashing.Sha256Hex("treść sprzed nowelizacji"))]);

        var summary = await Runner(pipeline, new InMemoryStore(a)).RunAsync("T", null, skip, default);

        Assert.Equal(["a"], pipeline.Processed);
        Assert.Equal(1, summary.Updated);
    }

    [Fact] // ODP-1: maxItems liczy także fast-skipy — parytet z dotychczasowym `processed++`
    public async Task MaxItems_counts_fast_skips_like_before()
    {
        var docs = new[] { Doc("a"), Doc("b"), Doc("c") };
        var skip = ProcessSkipSet.From([("a", Hashing.Sha256Hex(docs[0].RawContent))]);
        var pipeline = new ScriptedPipeline((_, _) => Ok());

        var summary = await Runner(pipeline, new InMemoryStore(docs)).RunAsync("T", 2, skip, default);

        Assert.Equal(2, summary.Total); // skip(a) + inserted(b); „c" poza limitem
        Assert.Equal(["b"], pipeline.Processed);
    }

    [Fact] // ODP-2: seria porażek = próg → ProcessAbortedException zamiast mielenia do końca magazynu
    public async Task Breaker_aborts_after_consecutive_failures()
    {
        var docs = Enumerable.Range(1, 30).Select(i => Doc($"d{i}")).ToArray();
        var pipeline = new ScriptedPipeline((_, _) => Fail());

        var ex = await Assert.ThrowsAsync<ProcessAbortedException>(() =>
            Runner(pipeline, new InMemoryStore(docs), failStreakLimit: 10)
                .RunAsync("T", null, ProcessSkipSet.Empty, default));

        Assert.Equal(10, pipeline.Processed.Count); // przerwane na progu, nie po 30
        Assert.Contains("T/d10", ex.Message);       // ostatni dokument wskazany po imieniu
        Assert.Contains("embed", ex.Message);       // z etapem
    }

    [Fact] // ODP-2: pojedynczy sukces w środku zeruje licznik — złe dokumenty nie przerywają runa
    public async Task Breaker_resets_on_any_success()
    {
        var docs = Enumerable.Range(1, 19).Select(i => Doc($"d{i}")).ToArray();
        var pipeline = new ScriptedPipeline((_, call) => call == 9 ? Ok() : Fail()); // 9×Failed, sukces, 9×Failed

        var summary = await Runner(pipeline, new InMemoryStore(docs), failStreakLimit: 10)
            .RunAsync("T", null, ProcessSkipSet.Empty, default);

        Assert.Equal(18, summary.Failed); // dojechał do końca
        Assert.Equal(1, summary.Inserted);
    }

    [Fact] // ODP-2: fast-skip też zeruje licznik (przewijanie po restarcie nie może dobić do progu)
    public async Task FastSkip_also_resets_streak()
    {
        var docs = Enumerable.Range(1, 19).Select(i => Doc($"d{i}")).ToArray();
        var skip = ProcessSkipSet.From([("d10", Hashing.Sha256Hex(docs[9].RawContent))]);
        var pipeline = new ScriptedPipeline((_, _) => Fail());

        var summary = await Runner(pipeline, new InMemoryStore(docs), failStreakLimit: 10)
            .RunAsync("T", null, skip, default);

        Assert.Equal(18, summary.Failed);
        Assert.Equal(1, summary.Skipped);
    }

    [Fact] // ODP-2: FailStreakLimit=0 wyłącza bezpiecznik
    public async Task Breaker_disabled_when_limit_zero()
    {
        var docs = Enumerable.Range(1, 25).Select(i => Doc($"d{i}")).ToArray();
        var pipeline = new ScriptedPipeline((_, _) => Fail());

        var summary = await Runner(pipeline, new InMemoryStore(docs), failStreakLimit: 0)
            .RunAsync("T", null, ProcessSkipSet.Empty, default);

        Assert.Equal(25, summary.Failed);
    }

    [Fact] // RÓWN-1: parallelism>1 przetwarza dokumenty NARAZ i liczy poprawnie (bez gubienia/dublowania)
    public async Task Parallel_processes_concurrently_and_counts_correctly()
    {
        var docs = Enumerable.Range(1, 40).Select(i => Doc($"d{i}")).ToArray();
        var pipeline = new ConcurrentPipeline();

        var summary = await Runner2(pipeline, new InMemoryStore(docs), parallelism: 8)
            .RunAsync("T", null, ProcessSkipSet.Empty, default);

        Assert.Equal(40, summary.Inserted);
        Assert.Equal(40, pipeline.Calls);
        Assert.True(pipeline.MaxConcurrent > 1, $"oczekiwano współbieżności, było {pipeline.MaxConcurrent}");
        Assert.True(pipeline.MaxConcurrent <= 8, $"przekroczono limit równoległości: {pipeline.MaxConcurrent}");
    }

    [Fact] // RÓWN-1: maxItems respektowany też przy równoległości (Bounded ucina strumień)
    public async Task Parallel_respects_max_items()
    {
        var docs = Enumerable.Range(1, 40).Select(i => Doc($"d{i}")).ToArray();
        var pipeline = new ConcurrentPipeline();

        var summary = await Runner2(pipeline, new InMemoryStore(docs), parallelism: 8)
            .RunAsync("T", 10, ProcessSkipSet.Empty, default);

        Assert.Equal(10, summary.Total);
        Assert.Equal(10, pipeline.Calls);
    }

    private RawProcessRunner Runner2(IIngestionPipeline pipeline, IRawDocumentStore store, int parallelism)
    {
        var services = new ServiceCollection();
        services.AddSingleton(pipeline);
        var sp = services.BuildServiceProvider();
        var opt = new ProcessOptions { FailureLogDir = _logDir, ProcessParallelism = parallelism };
        return new RawProcessRunner(
            sp.GetRequiredService<IServiceScopeFactory>(), store, Options.Create(opt),
            NullLogger<RawProcessRunner>.Instance);
    }

    [Fact] // ODP-1: klucz zbioru = dokładny ExternalId + hash (case-sensitive, jak klucz naturalny w DB)
    public void SkipSet_key_is_exact_id_plus_hash()
    {
        var set = ProcessSkipSet.From([("DU/2026/473", "abc123")]);

        Assert.True(set.Contains("DU/2026/473", "abc123"));
        Assert.False(set.Contains("DU/2026/473", "ABC123"));
        Assert.False(set.Contains("DU/2026/47", "abc123"));
        Assert.False(set.Contains("du/2026/473", "abc123"));
        Assert.Equal(1, set.Count);
        Assert.Equal(0, ProcessSkipSet.Empty.Count);
    }

    [Fact] // ODP-3: raport JSONL — parsowalna linia z pozycją, etapem i PEŁNYM błędem
    public async Task Failure_report_contains_full_error_with_stage()
    {
        var pipeline = new ScriptedPipeline((_, _) => Fail(stage: "chunk"));

        await Runner(pipeline, new InMemoryStore(Doc("zly-dokument")), failStreakLimit: 0)
            .RunAsync("T", null, ProcessSkipSet.Empty, default);

        var file = Assert.Single(Directory.GetFiles(_logDir, "process-failures-T-*.jsonl"));
        var line = Assert.Single(File.ReadAllLines(file));
        using var json = JsonDocument.Parse(line);
        Assert.Equal(1, json.RootElement.GetProperty("seq").GetInt32());
        Assert.Equal("zly-dokument", json.RootElement.GetProperty("externalId").GetString());
        Assert.Equal("chunk", json.RootElement.GetProperty("stage").GetString());
        Assert.Contains("TEI: connection refused", json.RootElement.GetProperty("error").GetString());
    }

    [Fact] // ODP-3: czysty run nie zostawia pustych plików raportu (tworzenie leniwe)
    public async Task Clean_run_leaves_no_report_file()
    {
        var pipeline = new ScriptedPipeline((_, _) => Ok());

        await Runner(pipeline, new InMemoryStore(Doc("a"))).RunAsync("T", null, ProcessSkipSet.Empty, default);

        Assert.False(Directory.Exists(_logDir)); // katalog też tworzony dopiero przy pierwszej porażce
    }
}
