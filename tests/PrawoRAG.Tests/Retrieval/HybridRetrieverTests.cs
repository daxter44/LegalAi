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
/// T-RETR — retrieval hybrydowy na żywym Postgresie. Bez TEI: <see cref="FakeEmbeddingProvider"/> daje
/// wektory deterministyczne, ale NIE semantyczne — więc asercje opierają się na zachowaniach odpornych na
/// szum toru gęstego: trafienie po DOKŁADNYM tekście (dystans cosine 0), dedup identycznych tekstów oraz
/// filtry SQL (MinChunkTokens, OnlyInForce). BM25 (tsvector) łapie unikalne tokeny niezależnie od wektorów.
/// Blokuje regresję usprawnień z gałęzi feat/e2-retrieval-quality.
/// </summary>
[Collection("LiveDb")]
public class HybridRetrieverTests
{
    private static readonly string Conn =
        Environment.GetEnvironmentVariable("PRAWORAG_DB")
        ?? "Host=localhost;Port=5432;Database=praworag;Username=praworag;Password=praworag";

    private static PrawoRagDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PrawoRagDbContext>().UseNpgsql(Conn, o => o.UseVector()).Options);

    private static async Task CleanAsync(params string[] sources)
    {
        await using var db = NewDb();
        await db.Documents.Where(d => sources.Contains(d.Source)).ExecuteDeleteAsync();
    }

    private static readonly FakeEmbeddingProvider Emb = new();

    private static async Task SeedAsync(string source, string extId, string docType, string text, int tokenCount, bool? inForce = null)
    {
        var vec = (await Emb.EmbedPassagesAsync([text], default))[0];
        await using var db = NewDb();
        var doc = new DocumentEntity
        {
            Id = Guid.CreateVersion7(), Source = source, ExternalId = extId, DocType = docType,
            Title = $"{source}/{extId}", ContentHash = $"{source}:{extId}", Status = DocumentStatus.Indexed,
            InForce = inForce, IngestedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(doc);
        db.Chunks.Add(new ChunkEntity
        {
            Id = Guid.CreateVersion7(), DocumentId = doc.Id, ChunkIndex = 0, Text = text,
            TokenCount = tokenCount, CharStart = 0, CharEnd = text.Length,
            Embedding = new Vector(vec), EmbeddedWith = Emb.ModelId,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<RetrievalResult> RetrieveAsync(RetrievalQuery query)
    {
        await using var db = NewDb();
        return await new HybridRetriever(db, Emb).RetrieveAsync(query, default);
    }

    [Fact] // R1: chunk trafiony po dokładnym tekście (dystans cosine 0 + BM25) jest w wynikach
    public async Task Retrieves_chunk_by_exact_text()
    {
        const string src = "TEST-RETR-1";
        await CleanAsync(src);
        const string text = "Zorptako unikalny przepis testowy alfa beta gamma delta epsilon";
        await SeedAsync(src, "c1", DocTypes.Act, text, tokenCount: 20, inForce: true);

        var res = await RetrieveAsync(new RetrievalQuery { Text = text, MinChunkTokens = 0 });
        Assert.Contains(res.Chunks, c => c.Text == text);
        await CleanAsync(src);
    }

    [Fact] // R2: identyczny tekst w wielu dokumentach kolapsuje do JEDNEGO wyniku (dedup)
    public async Task Deduplicates_identical_texts()
    {
        const string src = "TEST-RETR-2";
        await CleanAsync(src);
        const string text = "Deduptako wspolna formulka cytowana wielokrotnie w orzeczeniach testowych zeta";
        await SeedAsync(src, "d1", DocTypes.Judgment, text, tokenCount: 20);
        await SeedAsync(src, "d2", DocTypes.Judgment, text, tokenCount: 20);
        await SeedAsync(src, "d3", DocTypes.Judgment, text, tokenCount: 20);

        var res = await RetrieveAsync(new RetrievalQuery { Text = text, MinChunkTokens = 0 });
        Assert.Equal(1, res.Chunks.Count(c => c.Text == text)); // trzy kopie → jeden slot
        await CleanAsync(src);
    }

    [Fact] // R3: mini-chunk poniżej MinChunkTokens jest odsiany, mimo że tekst pasuje do zapytania
    public async Task Filters_out_mini_chunks_below_token_threshold()
    {
        const string src = "TEST-RETR-3";
        await CleanAsync(src);
        await SeedAsync(src, "mini", DocTypes.Judgment, "Minitako krotki", tokenCount: 3);
        await SeedAsync(src, "full", DocTypes.Judgment,
            "Minitako pelny przepis z wystarczajaca liczba tokenow do przejscia progu filtra", tokenCount: 25);

        var res = await RetrieveAsync(new RetrievalQuery { Text = "Minitako", MinChunkTokens = 20 });
        Assert.Contains(res.Chunks, c => c.Text.StartsWith("Minitako pelny"));
        Assert.DoesNotContain(res.Chunks, c => c.Text == "Minitako krotki"); // mini odsiane
        await CleanAsync(src);
    }

    [Fact] // R4: OnlyInForce wyklucza uchylony akt, przepuszcza obowiązujący i orzeczenia
    public async Task OnlyInForce_excludes_repealed_acts()
    {
        const string src = "TEST-RETR-4";
        await CleanAsync(src);
        await SeedAsync(src, "repealed", DocTypes.Act, "Inforcetako uchylony przepis testowy", tokenCount: 20, inForce: false);
        await SeedAsync(src, "active", DocTypes.Act, "Inforcetako obowiazujacy przepis testowy", tokenCount: 20, inForce: true);

        var res = await RetrieveAsync(new RetrievalQuery { Text = "Inforcetako", OnlyInForce = true, MinChunkTokens = 0 });
        Assert.Contains(res.Chunks, c => c.Text.Contains("obowiazujacy"));
        Assert.DoesNotContain(res.Chunks, c => c.Text.Contains("uchylony")); // akt nieobowiązujący odsiany
        await CleanAsync(src);
    }

    [Fact] // R5: reranker przestawia kolejność i to JEGO top-score steruje MaxSimilarity (bramka abstynencji)
    public async Task Reranker_reorders_and_drives_abstention_signal()
    {
        const string src = "TEST-RETR-5";
        await CleanAsync(src);
        await SeedAsync(src, "a", DocTypes.Judgment, "Rerankuj alfa zzztarget przepis testowy do wypromowania", tokenCount: 20);
        await SeedAsync(src, "b", DocTypes.Judgment, "Rerankuj beta zwykly przepis testowy bez boosta", tokenCount: 20);

        await using var db = NewDb();
        var reranker = new FakeReranker("zzztarget");
        var res = await new HybridRetriever(db, Emb, reranker).RetrieveAsync(
            new RetrievalQuery { Text = "Rerankuj przepis testowy", MinChunkTokens = 0 }, default);

        Assert.NotEmpty(res.Chunks);
        Assert.Contains("zzztarget", res.Chunks[0].Text);   // wypromowany na 1. miejsce
        Assert.Equal(0.99, res.Chunks[0].RerankScore);
        Assert.Equal(0.99, res.MaxSimilarity, 3);           // abstynencja gatuje na score rerankera, nie cosine
        Assert.True(reranker.Calls > 0);
        await CleanAsync(src);
    }
}
