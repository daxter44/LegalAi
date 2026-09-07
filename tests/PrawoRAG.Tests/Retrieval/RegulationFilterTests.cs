using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Storage;
using PrawoRAG.Storage.Entities;
using PrawoRAG.Storage.Retrieval;
using PrawoRAG.Tests.Fakes;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// T-REG — na żywym Postgresie: SAOS judgmentType=REGULATION (zarządzenia porządkowe, np. „doręczyć
/// odpis pełnomocnikowi") wykluczone z retrievalu, w obu torach (dense i sparse). Zdiagnozowane
/// 2026-07-18: krótkie, niemal identyczne teksty kancelaryjne dostawały sztucznie wysokie cosine
/// (0,84) do niezwiązanego pytania merytorycznego, zanieczyszczając wyniki bez wnoszenia treści.
/// </summary>
[Collection("LiveDb")]
public class RegulationFilterTests
{
    private static readonly string Conn =
        Environment.GetEnvironmentVariable("PRAWORAG_DB")
        ?? "Host=localhost;Port=5432;Database=praworag;Username=praworag;Password=praworag";

    private static PrawoRagDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PrawoRagDbContext>().UseNpgsql(Conn, o => o.UseVector()).Options);

    private static readonly FakeEmbeddingProvider Emb = new();

    private static async Task CleanAsync(params string[] sources)
    {
        await using var db = NewDb();
        await db.Documents.Where(d => sources.Contains(d.Source)).ExecuteDeleteAsync();
    }

    private static async Task SeedAsync(string source, string extId, string text, string? judgmentType)
    {
        var vec = (await Emb.EmbedPassagesAsync([text], default))[0];
        await using var db = NewDb();
        var doc = new DocumentEntity
        {
            Id = Guid.CreateVersion7(), Source = source, ExternalId = extId, DocType = DocTypes.Judgment,
            Title = $"{source}/{extId}", ContentHash = $"{source}:{extId}", Status = DocumentStatus.Indexed,
            IngestedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            TypedMetadata = judgmentType is null ? null : JsonDocument.Parse($$"""{"judgmentType":"{{judgmentType}}"}"""),
        };
        db.Documents.Add(doc);
        db.Chunks.Add(new ChunkEntity
        {
            Id = Guid.CreateVersion7(), DocumentId = doc.Id, ChunkIndex = 0, Text = text,
            TokenCount = 30, CharStart = 0, CharEnd = text.Length,
            Embedding = new Vector(vec), EmbeddedWith = Emb.ModelId,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<RetrievalResult> RetrieveAsync(RetrievalQuery query)
    {
        await using var db = NewDb();
        return await new HybridRetriever(db, Emb).RetrieveAsync(query, default);
    }

    [Fact] // R1: chunk dokumentu judgmentType=REGULATION wykluczony mimo trafienia (dense: FakeEmbeddingProvider
           // daje wszystkim seedowanym chunkom bliskie wektory — bez filtra by wszedł; sparse: unikalny token)
    public async Task Regulation_judgment_type_is_excluded()
    {
        const string src = "TEST-REG-1";
        await CleanAsync(src);
        await SeedAsync(src, "reg1", "ZARZĄDZENIE\nRegulatako1 odpis wyroku doręczyć pełnomocnikowi wnioskodawczyni", "REGULATION");

        var res = await RetrieveAsync(new RetrievalQuery { Text = "Regulatako1", MinChunkTokens = 0 });

        Assert.DoesNotContain(res.Chunks, c => c.Text.Contains("Regulatako1"));
        await CleanAsync(src);
    }

    [Fact] // R2: judgmentType=SENTENCE (albo brak metadanych) przechodzi normalnie — filtr nie nadgorliwy
    public async Task Non_regulation_judgment_type_passes_through()
    {
        const string src = "TEST-REG-2";
        await CleanAsync(src);
        await SeedAsync(src, "sent1", "Regulatako2 sąd zważył co następuje w przedmiocie odpowiedzialności", "SENTENCE");
        await SeedAsync(src, "nometa1", "Regulatako2b treść orzeczenia bez metadanych judgmentType w ogóle", null);

        var res = await RetrieveAsync(new RetrievalQuery { Text = "Regulatako2", MinChunkTokens = 0 });

        Assert.Contains(res.Chunks, c => c.Text.Contains("Regulatako2 sąd"));
        await CleanAsync(src);
    }
}
