using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Ingestion;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// Celowany reprocessing dokumentów Failed (rdzeń bez DB): odzyskane vs nadal-Failed vs brak surowego;
/// czyta po id (nie enumeruje magazynu); równoległość respektuje ProcessParallelism.
/// </summary>
public sealed class ReprocessFailedRunnerTests
{
    private static RawDocument Doc(string id) => new()
    {
        Source = "ELI", ExternalId = id, DocType = "act", RawContent = $"treść {id}",
    };

    private sealed class ByIdStore(params RawDocument[] docs) : IRawDocumentStore
    {
        public int EnumerateCalls { get; private set; }

        public Task<RawDocument?> ReadAsync(string source, string externalId, CancellationToken ct) =>
            Task.FromResult(docs.FirstOrDefault(d => d.Source == source && d.ExternalId == externalId));

        public Task<bool> ExistsAsync(string source, string externalId, CancellationToken ct) =>
            Task.FromResult(docs.Any(d => d.Source == source && d.ExternalId == externalId));
        public Task SaveAsync(RawDocument document, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountAsync(string source, CancellationToken ct) => Task.FromResult(docs.Length);

        public async IAsyncEnumerable<RawDocument> EnumerateAsync(string source, [EnumeratorCancellation] CancellationToken ct)
        {
            EnumerateCalls++; // reprocess NIE powinien tego wołać (czytamy po id)
            foreach (var d in docs) yield return d;
            await Task.CompletedTask;
        }
    }

    private sealed class ScriptedPipeline(Func<RawDocument, IngestResult> script) : IIngestionPipeline
    {
        public Task<IngestResult> ProcessAsync(RawDocument raw, CancellationToken ct) => Task.FromResult(script(raw));
    }

    private static ReprocessFailedRunner Runner(IIngestionPipeline pipeline, IRawDocumentStore store, int parallelism = 1)
    {
        var services = new ServiceCollection();
        services.AddSingleton(pipeline);
        var sp = services.BuildServiceProvider();
        var opt = new ProcessOptions { ProcessParallelism = parallelism };
        return new ReprocessFailedRunner(sp.GetRequiredService<IServiceScopeFactory>(), store, Options.Create(opt),
            NullLogger<ReprocessFailedRunner>.Instance);
    }

    private static IReadOnlyList<FailedDoc> Failed(params string[] ids) =>
        ids.Select(id => new FailedDoc(id, "[embed] TEI timeout", 1)).ToList();

    [Fact] // dokument, który tym razem przechodzi → recovered; czytany po id, bez enumeracji magazynu
    public async Task Recovers_previously_failed_document()
    {
        var store = new ByIdStore(Doc("DU/2024/1"), Doc("DU/2024/2"));
        var pipeline = new ScriptedPipeline(_ => new IngestResult(IngestOutcome.Inserted));

        var summary = await Runner(pipeline, store).RunAsync("ELI", Failed("DU/2024/1", "DU/2024/2"), default);

        Assert.Equal(2, summary.Recovered);
        Assert.Equal(0, summary.StillFailing);
        Assert.Equal(0, summary.MissingRaw);
        Assert.Equal(0, store.EnumerateCalls); // celowany odczyt, nie enumeracja 600k
    }

    [Fact] // awaria deterministyczna (nadal za długie) → StillFailing, nie recovered
    public async Task Still_failing_document_is_counted()
    {
        var store = new ByIdStore(Doc("DU/2024/1"));
        var pipeline = new ScriptedPipeline(_ =>
            new IngestResult(IngestOutcome.Failed, "embed", new InvalidOperationException("TEI timeout")));

        var summary = await Runner(pipeline, store).RunAsync("ELI", Failed("DU/2024/1"), default);

        Assert.Equal(0, summary.Recovered);
        Assert.Equal(1, summary.StillFailing);
    }

    [Fact] // brak surowego w magazynie → MissingRaw (nie da się reprocessować bez ponownego fetchu)
    public async Task Missing_raw_is_reported_not_processed()
    {
        var store = new ByIdStore(); // pusty magazyn
        var pipeline = new ScriptedPipeline(_ => throw new InvalidOperationException("nie powinno być wołane"));

        var summary = await Runner(pipeline, store).RunAsync("ELI", Failed("DU/2024/1"), default);

        Assert.Equal(1, summary.MissingRaw);
        Assert.Equal(0, summary.Total - summary.MissingRaw);
    }

    [Fact] // mieszany zestaw: 1 recovered + 1 nadal Failed + 1 bez surowego
    public async Task Mixed_outcomes()
    {
        var store = new ByIdStore(Doc("ok"), Doc("bad"));
        var pipeline = new ScriptedPipeline(raw => raw.ExternalId == "ok"
            ? new IngestResult(IngestOutcome.Updated)
            : new IngestResult(IngestOutcome.Failed, "embed", new InvalidOperationException("za długie")));

        var summary = await Runner(pipeline, store, parallelism: 4)
            .RunAsync("ELI", Failed("ok", "bad", "znikniety"), default);

        Assert.Equal(1, summary.Recovered);
        Assert.Equal(1, summary.StillFailing);
        Assert.Equal(1, summary.MissingRaw);
        Assert.Equal(3, summary.Total);
    }
}
