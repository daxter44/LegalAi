using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain;
using PrawoRAG.Ingestion;
using PrawoRAG.Ingestion.Chunking;
using PrawoRAG.Ingestion.Eli;
using PrawoRAG.Storage;
using PrawoRAG.Tests.Fakes;
using PrawoRAG.Tests.Fixtures;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// Akt ELI przez PEŁNY pipeline (normalize→chunk→embed→DB) na żywym Postgresie, bez TEI (Fake embedder).
/// Dowodzi: doc_type=act, mapowanie InForce, chunki = paragrafy artykułów (Section „Art. 148 § 1").
/// </summary>
[Collection("LiveDb")]
public class ActIngestionTests
{
    private static readonly string Conn =
        Environment.GetEnvironmentVariable("PRAWORAG_DB")
        ?? "Host=localhost;Port=5432;Database=praworag;Username=praworag;Password=praworag";

    private static PrawoRagDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PrawoRagDbContext>().UseNpgsql(Conn, o => o.UseVector()).Options);

    private static async Task CleanAsync(string source)
    {
        await using var db = NewDb();
        await db.Documents.Where(d => d.Source == source).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task Act_flows_through_pipeline_with_inforce_and_article_chunks()
    {
        const string src = "TEST-ACT-E2";
        await CleanAsync(src);
        var raw = EliFixtures.LoadAct("DU/1997/553") with { Source = src, ExternalId = "kk" };
        var embedder = new FakeEmbeddingProvider();
        var chunker = new TokenAwareChunker(embedder, Options.Create(new ChunkerOptions()));

        await using (var db = NewDb())
        {
            var pipeline = new IngestionPipeline(db, [new ActNormalizer()], chunker, embedder, NullLogger<IngestionPipeline>.Instance);
            Assert.Equal(IngestOutcome.Inserted, await pipeline.ProcessAsync(raw, default));
        }

        await using (var verify = NewDb())
        {
            var doc = await verify.Documents.Include(d => d.Chunks).SingleAsync(d => d.Source == src);
            Assert.Equal(DocTypes.Act, doc.DocType);
            Assert.Equal(true, doc.InForce);                                  // mapowanie InForce (nowa linia w pipeline)
            Assert.True(doc.Chunks.Count > 600, $"KK → setki chunków (per §); było {doc.Chunks.Count}");
            Assert.Contains(doc.Chunks, c => c.Section == "Art. 148 § 1");    // chunk = paragraf artykułu
        }
        await CleanAsync(src);
    }
}
