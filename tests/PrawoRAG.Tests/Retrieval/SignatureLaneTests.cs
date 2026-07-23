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
/// T-SIG-LANE — lane sygnatury na żywym Postgresie. Dowód: orzeczenie, którego TREŚĆ nie zawiera
/// własnej sygnatury, jest znajdowane po sygnaturze w pytaniu (exact-match po
/// <c>documents.CaseNumber</c>) — semantyka/BM25 by go nie zwróciły. Sygnatura SYNTETYCZNA
/// („IX ZZ 99999/99"), której nie ma w realnym korpusie, żeby test nie kolidował z prawdziwymi danymi.
/// </summary>
[Collection("LiveDb")]
public class SignatureLaneTests
{
    private const string Src = "TEST-SIG";
    private const string TargetText = "Sąd oddalił skargę na decyzję o warunkach zabudowy. Organ prawidłowo ustalił stan faktyczny.";

    private static readonly string Conn =
        Environment.GetEnvironmentVariable("PRAWORAG_DB")
        ?? "Host=localhost;Port=5432;Database=praworag;Username=praworag;Password=praworag";

    private static PrawoRagDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PrawoRagDbContext>().UseNpgsql(Conn, o => o.UseVector()).Options);

    private static readonly FakeEmbeddingProvider Emb = new();

    private static async Task CleanAsync()
    {
        await using var db = NewDb();
        // Sprząta też pozostałości po wcześniejszych wariantach testu (gdyby przerwał przed czyszczeniem).
        await db.Documents.Where(d => d.Source == Src || d.Source == "TEST-SIG-1" || d.Source == "TEST-SIG-2").ExecuteDeleteAsync();
    }

    private static async Task SeedJudgmentAsync(string extId, string caseNumberNormalized, string text)
    {
        var vec = (await Emb.EmbedPassagesAsync([text], default))[0];
        await using var db = NewDb();
        var doc = new DocumentEntity
        {
            Id = Guid.CreateVersion7(), Source = Src, ExternalId = extId, DocType = DocTypes.Judgment,
            Title = $"{Src}/{extId}", ContentHash = $"{Src}:{extId}", Status = DocumentStatus.Indexed,
            CaseNumber = caseNumberNormalized, IngestedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
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

    [Fact]
    public async Task Finds_judgment_by_signature_even_when_text_lacks_it()
    {
        await CleanAsync();
        try
        {
            // Treść BEZ własnej sygnatury (jak realny full_text NSA) + dystraktor z inną sygnaturą.
            await SeedJudgmentAsync("target", "IX ZZ 99999/99", TargetText);
            await SeedJudgmentAsync("distractor", "IX ZZ 11111/11",
                "Zupełnie inna sprawa dotycząca zezwolenia na usunięcie drzew i kar administracyjnych.");

            await using var db = NewDb();
            // Pytanie z sygnaturą w NATURALNYM formacie (małe litery, zwykłe spacje) — normalizacja klucza
            // dopasowuje do znormalizowanego CaseNumber mimo różnicy w zapisie.
            var res = await new HybridRetriever(db, Emb).RetrieveAsync(
                new RetrievalQuery { Text = "Co orzeczono w sprawie ix zz 99999/99?", MinChunkTokens = 0 }, default);

            // Trafienie dokładne (Score=MaxValue) na wierzchu — WŁAŚCIWE orzeczenie, mimo że jego treść
            // nie zawiera własnej sygnatury (semantyka/BM25 by go nie zwróciły).
            Assert.Equal(TargetText, res.Chunks[0].Text);
        }
        finally { await CleanAsync(); }
    }
}
