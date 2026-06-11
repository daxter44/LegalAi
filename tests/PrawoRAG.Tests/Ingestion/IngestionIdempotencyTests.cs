using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Documents;
using PrawoRAG.Domain.Embeddings;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Ingestion;
using PrawoRAG.Ingestion.Chunking;
using PrawoRAG.Ingestion.Saos;
using PrawoRAG.Storage;
using PrawoRAG.Storage.Entities;
using PrawoRAG.Tests.Fakes;
using PrawoRAG.Tests.Fixtures;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// T-IDEM — idempotencja ingestii na ŻYWYM Postgresie (kontener praworag-db-1).
/// Wymaga działającej bazy; każdy test izoluje się własnym kluczem Source i sprząta po sobie.
/// </summary>
[Collection("LiveDb")]
public class IngestionIdempotencyTests
{
    private static readonly string Conn =
        Environment.GetEnvironmentVariable("PRAWORAG_DB")
        ?? "Host=localhost;Port=5432;Database=praworag;Username=praworag;Password=praworag";

    private static PrawoRagDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PrawoRagDbContext>().UseNpgsql(Conn, o => o.UseVector()).Options);

    private static IngestionPipeline Pipeline(PrawoRagDbContext db, IEmbeddingProvider embedder)
    {
        var chunker = new TokenAwareChunker(embedder, Options.Create(new ChunkerOptions()));
        return new IngestionPipeline(db, [new JudgmentNormalizer()], chunker, embedder, NullLogger<IngestionPipeline>.Instance);
    }

    private static async Task CleanAsync(string source)
    {
        await using var db = NewDb();
        await db.Documents.Where(d => d.Source == source).ExecuteDeleteAsync();
    }

    private static RawDocument Raw(string source, string id, RawDocument fixture, string? content = null) =>
        fixture with { Source = source, ExternalId = id, RawContent = content ?? fixture.RawContent };

    [Fact] // T-IDEM #1: dwukrotny ingest tej samej próbki → 0 wywołań embeddingu w 2. przebiegu
    public async Task Second_run_does_no_embedding()
    {
        const string src = "TEST-IDEM-1";
        await CleanAsync(src);
        var raw = Raw(src, "j1", SaosFixtures.LoadJudgment(227221));

        var embedder = new FakeEmbeddingProvider();
        await using (var db = NewDb())
            Assert.Equal(IngestOutcome.Inserted, await Pipeline(db, embedder).ProcessAsync(raw, default));

        var afterFirst = embedder.PassageEmbedCalls;
        Assert.True(afterFirst > 0, "pierwszy przebieg powinien embedować");

        await using (var db = NewDb())
            Assert.Equal(IngestOutcome.Skipped, await Pipeline(db, embedder).ProcessAsync(raw, default));

        Assert.Equal(afterFirst, embedder.PassageEmbedCalls); // ZERO dodatkowych wywołań
        await CleanAsync(src);
    }

    [Fact] // T-IDEM #2: zmiana treści → tylko ten dokument re-procesowany, stare chunki zastąpione
    public async Task Content_change_replaces_chunks_without_orphans()
    {
        const string src = "TEST-IDEM-2";
        await CleanAsync(src);
        var fixture = SaosFixtures.LoadJudgment(227221);
        var embedder = new FakeEmbeddingProvider();

        await using (var db = NewDb())
            await Pipeline(db, embedder).ProcessAsync(Raw(src, "j1", fixture), default);

        Guid docId; int chunksBefore;
        await using (var db = NewDb())
        {
            var d = await db.Documents.Include(x => x.Chunks).SingleAsync(x => x.Source == src);
            docId = d.Id; chunksBefore = d.Chunks.Count;
        }

        // zmiana treści → inny hash
        var changed = Raw(src, "j1", fixture, fixture.RawContent + "\n<p>DODATKOWY AKAPIT TESTOWY.</p>");
        await using (var db = NewDb())
            Assert.Equal(IngestOutcome.Updated, await Pipeline(db, embedder).ProcessAsync(changed, default));

        await using (var verify = NewDb())
        {
            var d = await verify.Documents.SingleAsync(x => x.Source == src);
            Assert.Equal(docId, d.Id); // ten sam rekord (upsert po kluczu naturalnym)
            var totalChunks = await verify.Chunks.CountAsync(c => c.DocumentId == docId);
            var orphans = await verify.Chunks.CountAsync(c => !verify.Documents.Any(d2 => d2.Id == c.DocumentId));
            Assert.True(totalChunks > 0);
            Assert.Equal(0, orphans); // brak osieroconych po podmianie
        }
        await CleanAsync(src);
    }

    [Fact] // T-IDEM #3: przerwanie w połowie i restart → stan identyczny, bez duplikatów
    public async Task Restart_is_deterministic_no_duplicates()
    {
        const string src = "TEST-IDEM-3";
        await CleanAsync(src);
        var docs = new[]
        {
            Raw(src, "a", SaosFixtures.LoadJudgment(227221)),
            Raw(src, "b", SaosFixtures.LoadJudgment(31345)),
        };
        var embedder = new FakeEmbeddingProvider();

        // "przerwanie": przetwarzamy tylko pierwszy
        await using (var db = NewDb())
            await Pipeline(db, embedder).ProcessAsync(docs[0], default);

        // "restart": przetwarzamy wszystkie
        foreach (var d in docs)
            await using (var db = NewDb())
                await Pipeline(db, embedder).ProcessAsync(d, default);

        await using (var verify = NewDb())
        {
            Assert.Equal(2, await verify.Documents.CountAsync(x => x.Source == src)); // bez duplikatów
            foreach (var id in new[] { "a", "b" })
                Assert.Equal(1, await verify.Documents.CountAsync(x => x.Source == src && x.ExternalId == id));
        }
        await CleanAsync(src);
    }

    [Fact] // T-IDEM #5: dokument nieparsowalny (brak normalizera) → Failed + powód, nie wyjątek
    public async Task Unprocessable_document_is_quarantined()
    {
        const string src = "TEST-IDEM-5";
        await CleanAsync(src);
        var raw = new RawDocument { Source = src, ExternalId = "bad", DocType = "nieznany-typ", RawContent = "x" };

        await using (var db = NewDb())
            Assert.Equal(IngestOutcome.Failed, await Pipeline(db, new FakeEmbeddingProvider()).ProcessAsync(raw, default));

        await using (var verify = NewDb())
        {
            var d = await verify.Documents.SingleAsync(x => x.Source == src);
            Assert.Equal(DocumentStatus.Failed, d.Status);
            Assert.False(string.IsNullOrEmpty(d.FailureReason));
            Assert.Equal(1, d.AttemptCount);
        }
        await CleanAsync(src);
    }

    [Fact] // T-IDEM #6: zmiana modelu embeddingów → re-embed (tylko niezgodne), bez re-normalizacji
    public async Task Model_change_triggers_reembed_only()
    {
        const string src = "TEST-IDEM-6";
        await CleanAsync(src);
        var raw = Raw(src, "j1", SaosFixtures.LoadJudgment(227221));

        await using (var db = NewDb())
            await Pipeline(db, new FakeEmbeddingProvider(modelId: "fake@v1")).ProcessAsync(raw, default);

        var v2 = new FakeEmbeddingProvider(modelId: "fake@v2");
        await using (var db = NewDb())
            Assert.Equal(IngestOutcome.ReEmbedded, await Pipeline(db, v2).ProcessAsync(raw, default));

        Assert.True(v2.PassageEmbedCalls > 0);
        await using (var verify = NewDb())
        {
            var d = await verify.Documents.Include(x => x.Chunks).SingleAsync(x => x.Source == src);
            Assert.All(d.Chunks, c => Assert.Equal("fake@v2", c.EmbeddedWith)); // wszystkie przemodelowane
        }
        await CleanAsync(src);
    }
}

[CollectionDefinition("LiveDb", DisableParallelization = true)]
public class LiveDbCollection;
