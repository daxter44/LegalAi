using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Documents;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Storage;
using PrawoRAG.Storage.Entities;
using PrawoRAG.Storage.Retrieval;
using PrawoRAG.Tests.Fakes;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// T-ACT-CACHE — memoizacja rozpoznania aktu nie może zmienić WYNIKÓW, tylko koszt.
///
/// `ResolveActAsync` jest wywoływane do 6× na retrieval (do 4× tor strukturalny + do 2× most cytowań)
/// i praktycznie zawsze o tę samą wskazówkę, a każde wywołanie to skan tytułów aktów (`ILIKE '%…%'`
/// albo `similarity()` bez indeksu GIN trgm). Memoizacja per-instancja (retriever jest `AddScoped`,
/// więc zasięg = jedno żądanie) zbiera te wywołania do jednego per wskazówka.
///
/// Realne ryzyko takiej zmiany to POMIESZANIE wpisów: cache zwracający wynik jednego kodeksu dla
/// drugiego. Test pilnuje dokładnie tego — jedno pytanie cytujące DWA różne kodeksy musi dociągnąć
/// artykuły z OBU, a nie dwa razy z tego samego.
/// </summary>
[Collection("LiveDb")]
public class ActResolutionCacheTests
{
    private const string Src = "TEST-ACT-CACHE";

    private static readonly string Conn =
        Environment.GetEnvironmentVariable("PRAWORAG_DB")
        ?? "Host=localhost;Port=5432;Database=praworag;Username=praworag;Password=praworag";

    private static PrawoRagDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PrawoRagDbContext>().UseNpgsql(Conn, o => o.UseVector()).Options);

    private static readonly FakeEmbeddingProvider Emb = new();

    private static async Task CleanAsync()
    {
        await using var db = NewDb();
        await db.Documents.Where(d => d.Source == Src).ExecuteDeleteAsync();
    }

    /// <summary>Jeden dokument-akt z wieloma artykułami — tak wygląda realny korpus (resolver zwraca
    /// JEDEN <c>ExternalId</c> na akt, więc artykuły rozsypane po osobnych dokumentach byłyby dla toru
    /// strukturalnego nieosiągalne).</summary>
    private static async Task SeedActAsync(string extId, string title, params (string Article, string Text)[] articles)
    {
        await using var db = NewDb();
        var doc = new DocumentEntity
        {
            Id = Guid.CreateVersion7(), Source = Src, ExternalId = extId, DocType = DocTypes.Act,
            Title = title, ContentHash = $"{Src}:{extId}", Status = DocumentStatus.Indexed, InForce = true,
            IngestedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(doc);

        for (var i = 0; i < articles.Length; i++)
        {
            var (article, text) = articles[i];
            var vec = (await Emb.EmbedPassagesAsync([text], default))[0];
            db.Chunks.Add(new ChunkEntity
            {
                Id = Guid.CreateVersion7(), DocumentId = doc.Id, ChunkIndex = i, Text = text,
                TokenCount = 30, CharStart = 0, CharEnd = text.Length, ArticleNo = article,
                Embedding = new Vector(vec), EmbeddedWith = Emb.ModelId,
                Locator = JsonSerializer.SerializeToDocument(new CitationLocator { EliId = extId, Article = article }),
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact] // CACHE1: dwa cytaty tego samego aktu → drugie rozpoznanie idzie z cache i nadal dociąga swój artykuł
    public async Task Second_citation_of_same_act_still_fetches_its_article()
    {
        await CleanAsync();
        try
        {
            // Dwa artykuły JEDNEGO aktu (tytuł zgodny z mapą aliasów: „KC" → „Kodeks cywilny").
            await SeedActAsync("TEST/KC/1", "Kodeks cywilny (test)",
                ("415", "Zorptako pierwszy: kto z winy swej wyrzadzil drugiemu szkode obowiazany jest do jej naprawienia."),
                ("416", "Zorptako drugi: odpowiedzialnosc za szkode wyrzadzona przez podwladnego przy wykonywaniu czynnosci."));

            await using var db = NewDb();
            var res = await new HybridRetriever(db, Emb).RetrieveAsync(
                new RetrievalQuery
                {
                    Text = "Jak sie ma art. 415 do art. 416 KC?",
                    MinChunkTokens = 0,
                    CitationBridgeArticles = 0, // izolujemy tor strukturalny od mostu cytowań
                },
                default);

            // Tor strukturalny rozwiązuje akt DWA razy (raz per cytat) — drugi raz z memoizacji.
            // Gdyby cache zwracał zły wpis (albo psuł drugie wywołanie), drugi artykuł by nie wszedł
            // torem dokładnym.
            Assert.Contains(res.Chunks, c => c.Text.Contains("Zorptako pierwszy"));
            Assert.Contains(res.Chunks, c => c.Text.Contains("Zorptako drugi"));
            Assert.True(res.ExactMatchHits >= 2, $"oczekiwano ≥2 trafień dokładnych, było {res.ExactMatchHits}");
        }
        finally { await CleanAsync(); }
    }

    [Fact] // CACHE2: powtórzony retrieval na TEJ SAMEJ instancji (jak przy follow-upie) daje ten sam wynik
    public async Task Repeated_retrieval_on_same_instance_is_stable()
    {
        await CleanAsync();
        try
        {
            await SeedActAsync("TEST/KC/2", "Kodeks cywilny (test)",
                ("415", "Zorptako cywilny: kto z winy swej wyrzadzil drugiemu szkode obowiazany jest do jej naprawienia."));

            await using var db = NewDb();
            // JEDNA instancja retrievera obsługuje oba przebiegi — dokładnie jak FollowUpSelector,
            // który woła RetrieveAsync dwa razy (wariant surowy i kontekstowy) na tym samym obiekcie.
            var retriever = new HybridRetriever(db, Emb);
            var query = new RetrievalQuery
            {
                Text = "Co mowi art. 415 KC?", MinChunkTokens = 0, CitationBridgeArticles = 0,
            };

            var first = await retriever.RetrieveAsync(query, default);
            var second = await retriever.RetrieveAsync(query, default);

            Assert.Equal(
                first.Chunks.Select(c => c.ChunkId).ToArray(),
                second.Chunks.Select(c => c.ChunkId).ToArray());
            Assert.Equal(first.ExactMatchHits, second.ExactMatchHits);
        }
        finally { await CleanAsync(); }
    }
}
