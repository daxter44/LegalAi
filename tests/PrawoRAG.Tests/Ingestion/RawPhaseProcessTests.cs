using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Documents;
using PrawoRAG.Domain.Embeddings;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Ingestion;
using PrawoRAG.Ingestion.Chunking;
using PrawoRAG.Ingestion.Saos;
using PrawoRAG.Ingestion.Storage;
using PrawoRAG.Storage;
using PrawoRAG.Tests.Fakes;
using PrawoRAG.Tests.Fixtures;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// Faza „process" na ŻYWYM Postgresie (kontener praworag-db-1). Dowodzi: przetwarzanie z magazynu
/// działa offline (C2), daje wynik identyczny jak bezpośredni pipeline (C3), a po zmianie chunkera
/// przelicza chunki z TEGO SAMEGO magazynu bez pobierania (C4). Sekcja „C" planu.
/// </summary>
[Collection("LiveDb")]
public sealed class RawPhaseProcessTests : IDisposable
{
    private static readonly string Conn =
        Environment.GetEnvironmentVariable("PRAWORAG_DB")
        ?? "Host=localhost;Port=5432;Database=praworag;Username=praworag;Password=praworag";

    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var r in _roots)
            if (Directory.Exists(r)) Directory.Delete(r, recursive: true);
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "praworag-process-tests", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    private FileSystemRawDocumentStore NewStore(out string root)
    {
        root = NewRoot();
        return new FileSystemRawDocumentStore(Options.Create(new RawStoreOptions { RootPath = root }));
    }

    private static PrawoRagDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PrawoRagDbContext>().UseNpgsql(Conn, o => o.UseVector()).Options);

    private static async Task CleanAsync(string source)
    {
        await using var db = NewDb();
        await db.Documents.Where(d => d.Source == source).ExecuteDeleteAsync();
    }

    private static IngestionPipeline Pipeline(PrawoRagDbContext db, IEmbeddingProvider embedder)
    {
        var chunker = new TokenAwareChunker(embedder, Options.Create(new ChunkerOptions()));
        return new IngestionPipeline(db, [new JudgmentNormalizer()], chunker, embedder, NullLogger<IngestionPipeline>.Instance);
    }

    /// <summary>Provider fazy „process": pipeline z żywą bazą + Fake embedder (bez TEI). Brak ISourceConnector
    /// — gdyby process próbował pobierać, nie miałby skąd.</summary>
    private static ServiceProvider BuildProcessProvider(
        IRawDocumentStore store, IEmbeddingProvider embedder, Action<ChunkerOptions>? configureChunker = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<PrawoRagDbContext>(o => o.UseNpgsql(Conn, x => x.UseVector()));
        services.AddSingleton(embedder);
        services.AddSingleton<IDocumentNormalizer, JudgmentNormalizer>();
        services.AddOptions<ChunkerOptions>().Configure(configureChunker ?? (_ => { }));
        services.AddTransient<IChunker, TokenAwareChunker>();
        services.AddScoped<IngestionPipeline>();
        services.AddSingleton(store);
        services.AddSingleton<RawProcessRunner>();
        return services.BuildServiceProvider();
    }

    [Fact] // C2: process działa OFFLINE z magazynu (bez sieci/TEI/konektora)
    public async Task Process_runs_offline_from_store()
    {
        const string src = "TEST-C2";
        await CleanAsync(src);
        var store = NewStore(out _);
        await store.SaveAsync(SaosFixtures.LoadJudgment(227221) with { Source = src, ExternalId = "j1" }, default);

        var embedder = new FakeEmbeddingProvider();
        await using var sp = BuildProcessProvider(store, embedder);

        var summary = await sp.GetRequiredService<RawProcessRunner>().RunAsync(src, null, default);
        Assert.Equal(1, summary.Inserted);
        Assert.True(embedder.PassageEmbedCalls > 0);

        await using var db = NewDb();
        var doc = await db.Documents.Include(d => d.Chunks).SingleAsync(d => d.Source == src);
        Assert.Equal(DocumentStatus.Indexed, doc.Status);
        Assert.True(doc.Chunks.Count > 0);
        await CleanAsync(src);
    }

    [Fact] // C3: process z magazynu == bezpośredni pipeline (równoważność E2E)
    public async Task Process_from_store_equals_direct_pipeline()
    {
        const string srcA = "TEST-C3-A", srcB = "TEST-C3-B";
        await CleanAsync(srcA);
        await CleanAsync(srcB);
        var fixture = SaosFixtures.LoadJudgment(227221);

        // A — bezpośredni pipeline
        await using (var db = NewDb())
            await Pipeline(db, new FakeEmbeddingProvider()).ProcessAsync(fixture with { Source = srcA, ExternalId = "j1" }, default);

        // B — magazyn → RawProcessRunner
        var store = NewStore(out _);
        await store.SaveAsync(fixture with { Source = srcB, ExternalId = "j1" }, default);
        await using (var sp = BuildProcessProvider(store, new FakeEmbeddingProvider()))
            await sp.GetRequiredService<RawProcessRunner>().RunAsync(srcB, null, default);

        await using (var db = NewDb())
        {
            var a = await db.Documents.Include(d => d.Chunks).SingleAsync(d => d.Source == srcA);
            var b = await db.Documents.Include(d => d.Chunks).SingleAsync(d => d.Source == srcB);
            Assert.Equal(a.ContentHash, b.ContentHash);
            Assert.Equal(a.Chunks.Count, b.Chunks.Count);
            Assert.Equal(
                a.Chunks.OrderBy(c => c.ChunkIndex).Select(c => c.Text),
                b.Chunks.OrderBy(c => c.ChunkIndex).Select(c => c.Text));
            Assert.Equal(
                a.Chunks.OrderBy(c => c.ChunkIndex).Select(c => c.EmbeddedWith),
                b.Chunks.OrderBy(c => c.ChunkIndex).Select(c => c.EmbeddedWith));
        }
        await CleanAsync(srcA);
        await CleanAsync(srcB);
    }

    [Fact] // C4: zmiana chunkera → re-chunk z TEGO SAMEGO magazynu, bez pobierania
    public async Task Reprocessing_with_new_chunker_rechunks_from_store_without_fetch()
    {
        const string src = "TEST-C4";
        await CleanAsync(src);
        var store = NewStore(out _);
        await store.SaveAsync(SaosFixtures.LoadJudgment(227221) with { Source = src, ExternalId = "j1" }, default);

        // Przebieg A — większe chunki.
        await using (var sp = BuildProcessProvider(store, new FakeEmbeddingProvider(),
                         o => { o.TargetTokens = 450; o.MaxTokens = 512; o.OverlapTokens = 80; }))
            await sp.GetRequiredService<RawProcessRunner>().RunAsync(src, null, default);

        int countA;
        string hashA;
        await using (var db = NewDb())
        {
            var d = await db.Documents.Include(x => x.Chunks).SingleAsync(x => x.Source == src);
            countA = d.Chunks.Count;
            hashA = d.ContentHash;
        }

        // Treść się nie zmienia → pipeline pomija po content_hash. Aby wymusić re-chunk czyścimy dokumenty
        // (lokalnie, BEZ pobierania) — to realny workflow re-processingu z magazynu.
        await CleanAsync(src);

        // Przebieg B — dużo mniejsze chunki, z TEGO SAMEGO magazynu (zero fetchu).
        await using (var sp = BuildProcessProvider(store, new FakeEmbeddingProvider(),
                         o => { o.TargetTokens = 100; o.MaxTokens = 120; o.OverlapTokens = 20; }))
            await sp.GetRequiredService<RawProcessRunner>().RunAsync(src, null, default);

        int countB, orphans;
        string hashB;
        await using (var db = NewDb())
        {
            var d = await db.Documents.Include(x => x.Chunks).SingleAsync(x => x.Source == src);
            countB = d.Chunks.Count;
            hashB = d.ContentHash;
            orphans = await db.Chunks.CountAsync(c => !db.Documents.Any(dd => dd.Id == c.DocumentId));
        }

        Assert.True(countB > countA, $"mniejsze chunki → więcej chunków (A={countA}, B={countB})");
        Assert.Equal(hashA, hashB); // ta sama treść źródłowa, tylko inny podział
        Assert.Equal(0, orphans);
        await CleanAsync(src);
    }
}
