using Microsoft.Extensions.DependencyInjection;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Ingestion;
using PrawoRAG.Ingestion.Storage;
using PrawoRAG.Tests.Fakes;
using PrawoRAG.Tests.Fixtures;
using Microsoft.Extensions.Options;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// C1 — faza „fetch" jest idempotentna i nie wymaga bazy: drugi przebieg pomija już pobrane,
/// zero ponownych zapisów. Magazyn = temp dir; checkpoint sync_state best-effort (brak bazy → warning).
/// </summary>
public sealed class RawPhaseFetchTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "praworag-fetch-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact] // C1: fetch pomija istniejące (idempotencja), zero ponownych zapisów
    public async Task Fetch_skips_already_stored_documents()
    {
        var docs = new[]
        {
            SaosFixtures.LoadJudgment(227221) with { ExternalId = "a" },
            SaosFixtures.LoadJudgment(31345) with { ExternalId = "b" },
            SaosFixtures.LoadJudgment(227221) with { ExternalId = "c" },
        };
        var connector = new FakeSourceConnector(SourceKeys.Saos, docs);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IRawDocumentStore>(
            new FileSystemRawDocumentStore(Options.Create(new RawStoreOptions { RootPath = _root })));
        services.AddSingleton<ISourceConnector>(connector);
        services.AddSingleton<RawFetchRunner>();
        await using var sp = services.BuildServiceProvider();

        var fetch = sp.GetRequiredService<RawFetchRunner>();
        var store = sp.GetRequiredService<IRawDocumentStore>();

        var first = await fetch.RunAsync(SourceKeys.Saos, new FetchRequest(), default);
        Assert.Equal(3, first.Fetched);
        Assert.Equal(0, first.SkippedExisting);
        Assert.Equal(3, await store.CountAsync(SourceKeys.Saos, default));

        var second = await fetch.RunAsync(SourceKeys.Saos, new FetchRequest(), default);
        Assert.Equal(0, second.Fetched);          // nic nowego nie zapisano
        Assert.Equal(3, second.SkippedExisting);  // wszystkie pominięte
        Assert.Equal(3, await store.CountAsync(SourceKeys.Saos, default)); // bez duplikatów
    }
}
