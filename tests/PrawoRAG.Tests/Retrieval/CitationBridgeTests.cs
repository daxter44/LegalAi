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
/// T-MOST — most cytowań na żywym Postgresie (diagnoza „statut nieretrievalny" 2026-07-17 + sonda
/// --probe-akty 2026-07-18): przepis rządzący dociągany z cytowań w trafionych orzeczeniach.
/// Jak w <see cref="HybridRetrieverTests"/>: FakeEmbeddingProvider nie jest semantyczny, więc
/// orzeczenia-kandydatów łapiemy unikalnym tokenem przez BM25; most działa na TEKSTACH kandydatów,
/// więc reszta scenariusza jest deterministyczna.
/// </summary>
[Collection("LiveDb")]
public class CitationBridgeTests
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

    /// <summary>Seed z kontrolowanym tytułem dokumentu (resolver aktów szuka po tytule ILIKE)
    /// i opcjonalnym <c>ArticleNo</c> chunka (dociąganie artykułu idzie po metadanych).</summary>
    private static async Task SeedAsync(
        string source, string extId, string docType, string title, string text, string? articleNo = null)
    {
        var vec = (await Emb.EmbedPassagesAsync([text], default))[0];
        await using var db = NewDb();
        var doc = new DocumentEntity
        {
            Id = Guid.CreateVersion7(), Source = source, ExternalId = extId, DocType = docType,
            Title = title, ContentHash = $"{source}:{extId}", Status = DocumentStatus.Indexed,
            IngestedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(doc);
        db.Chunks.Add(new ChunkEntity
        {
            Id = Guid.CreateVersion7(), DocumentId = doc.Id, ChunkIndex = 0, Text = text,
            ArticleNo = articleNo, TokenCount = 30, CharStart = 0, CharEnd = text.Length,
            Embedding = new Vector(vec), EmbeddedWith = Emb.ModelId,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<RetrievalResult> RetrieveAsync(RetrievalQuery query)
    {
        await using var db = NewDb();
        return await new HybridRetriever(db, Emb).RetrieveAsync(query, default);
    }

    // Fikcyjny artykuł 415 w akcie o tytule zawierającym „Kodeks cywilny" (wymóg resolvera aliasów).
    // Unikalne tokeny per test, żeby BM25 nie mieszał scenariuszy między testami.

    [Fact] // M1: ≥2 NIEZALEŻNE orzeczenia cytują art. 415 k.c. → chunk KC dołożony na czoło wyników
    public async Task Two_judgments_citing_statute_pull_it_into_results()
    {
        const string src = "TEST-MOST-1";
        await CleanAsync(src);
        await SeedAsync(src, "kc", DocTypes.Act, "Kodeks cywilny (testowy Mostako1)",
            "Kto z winy swej wyrządził drugiemu szkodę, obowiązany jest do jej naprawienia. Mostakoprzepis.",
            articleNo: "415");
        await SeedAsync(src, "j1", DocTypes.Judgment, "SO w Testowie I C 1/24",
            "Mostako1 wichura przewróciła drzewo na ogrodzenie; podstawą odpowiedzialności jest art. 415 k.c. i wina właściciela.");
        await SeedAsync(src, "j2", DocTypes.Judgment, "SR w Testowie I C 2/24",
            "Mostako1 topola runęła na altankę; sąd rozważał przesłanki z art. 415 k.c. oraz siłę wyższą jako okoliczność zwalniającą.");

        var res = await RetrieveAsync(new RetrievalQuery { Text = "Mostako1 drzewo szkoda", MinChunkTokens = 0 });

        Assert.Contains(res.Chunks, c => c.DocType == DocTypes.Act && c.Text.Contains("Mostakoprzepis"));
        // Norma jako kotwica: przed orzeczeniami (brak toru strukturalnego — pytanie bez jawnego cytatu).
        Assert.Equal(DocTypes.Act, res.Chunks[0].DocType);
        await CleanAsync(src);
    }

    [Fact] // M2: próg głosów — JEDNO orzeczenie cytujące to za mało (koszt złego przepisu > koszt braku)
    public async Task Single_citing_judgment_is_below_vote_threshold()
    {
        const string src = "TEST-MOST-2";
        await CleanAsync(src);
        await SeedAsync(src, "kc", DocTypes.Act, "Kodeks cywilny (testowy Mostako2)",
            "Treść przepisu testowego mostu. Mostakodwaprzepis.", articleNo: "415");
        await SeedAsync(src, "j1", DocTypes.Judgment, "SO w Testowie I C 3/24",
            "Mostako2 drzewo spadło na samochód; podstawą jest art. 415 k.c. i domniemanie winy.");
        await SeedAsync(src, "j2", DocTypes.Judgment, "SR w Testowie I C 4/24",
            "Mostako2 drzewo spadło na płot; powództwo oddalono bez wskazania podstawy prawnej.");

        var res = await RetrieveAsync(new RetrievalQuery { Text = "Mostako2 drzewo szkoda", MinChunkTokens = 0 });

        Assert.DoesNotContain(res.Chunks, c => c.Text.Contains("Mostakodwaprzepis"));
        await CleanAsync(src);
    }

    [Fact] // M3: CitationBridgeArticles=0 wyłącza most — zachowanie sprzed zmiany (zero regresji)
    public async Task Bridge_disabled_restores_previous_behaviour()
    {
        const string src = "TEST-MOST-3";
        await CleanAsync(src);
        await SeedAsync(src, "kc", DocTypes.Act, "Kodeks cywilny (testowy Mostako3)",
            "Treść przepisu testowego mostu. Mostakotrzyprzepis.", articleNo: "415");
        await SeedAsync(src, "j1", DocTypes.Judgment, "SO w Testowie I C 5/24",
            "Mostako3 lipa runęła na garaż; odpowiedzialność z art. 415 k.c. wymaga winy.");
        await SeedAsync(src, "j2", DocTypes.Judgment, "SR w Testowie I C 6/24",
            "Mostako3 sosna złamała się w wichurze; sąd przywołał art. 415 k.c. i brak zawinienia.");

        var res = await RetrieveAsync(new RetrievalQuery
        {
            Text = "Mostako3 drzewo szkoda", MinChunkTokens = 0, CitationBridgeArticles = 0,
        });

        Assert.DoesNotContain(res.Chunks, c => c.Text.Contains("Mostakotrzyprzepis"));
        await CleanAsync(src);
    }

    [Fact] // M4: most nie dotyka sygnału abstynencji — MaxSimilarity identyczne z mostem i bez
    public async Task Bridge_does_not_change_abstention_signal()
    {
        const string src = "TEST-MOST-4";
        await CleanAsync(src);
        await SeedAsync(src, "kc", DocTypes.Act, "Kodeks cywilny (testowy Mostako4)",
            "Treść przepisu testowego mostu. Mostakoczteryprzepis.", articleNo: "415");
        await SeedAsync(src, "j1", DocTypes.Judgment, "SO w Testowie I C 7/24",
            "Mostako4 dąb przewrócił się na ogrodzenie; podstawa to art. 415 k.c. i należyta staranność.");
        await SeedAsync(src, "j2", DocTypes.Judgment, "SR w Testowie I C 8/24",
            "Mostako4 brzoza uszkodziła wiatę; sąd zastosował art. 415 k.c. wobec zaniedbań pielęgnacji.");

        var with = await RetrieveAsync(new RetrievalQuery { Text = "Mostako4 drzewo szkoda", MinChunkTokens = 0 });
        var without = await RetrieveAsync(new RetrievalQuery
        {
            Text = "Mostako4 drzewo szkoda", MinChunkTokens = 0, CitationBridgeArticles = 0,
        });

        Assert.Contains(with.Chunks, c => c.Text.Contains("Mostakoczteryprzepis")); // most zadziałał…
        Assert.Equal(without.MaxSimilarity, with.MaxSimilarity, 10);                // …a bramka bez zmian
        await CleanAsync(src);
    }
}
