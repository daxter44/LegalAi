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
/// T-VAC-LIVE — most vacatio legis na żywym Postgresie. Odtwarza przypadek źródłowy
/// (DIAGNOZA-NOWELIZACJA-DATA-WEJSCIA-W-ZYCIE-2026-08-27): pytanie o datę trafia w klauzulę wejścia
/// w życie, a treść wskazanych w niej przepisów jest nieretrievalna (zmierzone rangi #2367/#50430/#82405
/// przy dokładnym skanie), więc system odmawiał, mając w ręku samą klauzulę.
///
/// Zasiew celowo naśladuje ten kształt: klauzula zawiera datę z pytania, a treść zmian nie zawiera
/// ŻADNEGO słowa z pytania — więc jeśli treść wejdzie do wyniku, to wyłącznie strukturalnie.
/// </summary>
[Collection("LiveDb")]
public class VacatioLegisLiveTests
{
    private static readonly string Conn =
        Environment.GetEnvironmentVariable("PRAWORAG_DB")
        ?? "Host=localhost;Port=5432;Database=praworag;Username=praworag;Password=praworag";

    private static PrawoRagDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PrawoRagDbContext>().UseNpgsql(Conn, o => o.UseVector()).Options);

    private static readonly FakeEmbeddingProvider Emb = new();

    private const string Source = "TEST-VACATIO";

    /// <summary>Klauzula: jedyny fragment aktu niosący sygnał czasowy z pytania.</summary>
    private const string Clause =
        "Ustawa nowelizujaca, Art. 13\nArt. 13. Ustawa wchodzi w życie po upływie 14 dni od dnia "
        + "ogłoszenia, z wyjątkiem art. 1 pkt 1 lit. c oraz pkt 3, które wchodzą w życie z dniem "
        + "20 wrzesnia 2026 r.";

    /// <summary>Treść zmian: sucha proza legislacyjna, zero słów z pytania o datę.</summary>
    private static readonly (string Article, string Text)[] Content =
    [
        ("1", "Ustawa nowelizujaca, Art. 1\nArt. 1. W ustawie zmienia sie nastepujace przepisy: wstep enumeracji."),
        ("1", "Ustawa nowelizujaca, Art. 1\npkt 1 lit. c) w slowniczku dodaje sie definicje kotwotronu i plaskownika."),
        ("1", "Ustawa nowelizujaca, Art. 1\npkt 3) art. 9 ust. 3 otrzymuje brzmienie: organ rozstrzyga w terminie 45 dni."),
        ("7", "Ustawa nowelizujaca, Art. 7\nArt. 7. Przepisy przejsciowe dotyczace postepowan w toku."),
    ];

    private static async Task CleanAsync()
    {
        await using var db = NewDb();
        await db.Documents.Where(d => d.Source == Source).ExecuteDeleteAsync();
    }

    private static async Task SeedAsync()
    {
        var texts = new[] { Clause }.Concat(Content.Select(c => c.Text)).ToList();
        var vecs = await Emb.EmbedPassagesAsync(texts, default);

        await using var db = NewDb();
        var doc = new DocumentEntity
        {
            Id = Guid.CreateVersion7(), Source = Source, ExternalId = "DU/2025/9999", DocType = DocTypes.Act,
            Title = $"{Source} — ustawa o zmianie ustawy Prawo budowlane", ContentHash = $"{Source}:1",
            Status = DocumentStatus.Indexed, InForce = true,
            IngestedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(doc);

        // Klauzula ma ArticleNo=13, treść zmian ArticleNo=1 (jeden wielki artykuł pocięty na chunki —
        // dokładnie tak leży w bazie akt nowelizujący) i art. 7 jako kontrola: NIE jest wskazany
        // w klauzuli, więc most nie ma prawa go dociągnąć.
        //
        // NIERETRIEWALNOŚĆ treści odtwarzamy DETERMINISTYCZNIE, przez `TokenCount` poniżej progu
        // `MinChunkTokens` z zapytania — a nie przez podobieństwo, bo atrapa embeddera liczy wektor
        // z hasha tekstu (losowanego per proces), więc „co wygra semantycznie" nie jest powtarzalne.
        // Test dowodzi przy okazji realnej właściwości mostu: musi OMIJAĆ próg minimalnej długości
        // chunka, bo treść nowelizacji bywa krótka (ta sama zasada, co w torach exact-match — P5).
        var articles = new[] { "13" }.Concat(Content.Select(c => c.Article)).ToArray();
        for (var i = 0; i < texts.Count; i++)
            db.Chunks.Add(new ChunkEntity
            {
                Id = Guid.CreateVersion7(), DocumentId = doc.Id, ChunkIndex = i, Text = texts[i],
                ArticleNo = articles[i],
                // Retrievalna jest TYLKO klauzula (i=0). Treść art. 1 ORAZ kontrolny art. 7 są poniżej
                // progu, więc ich obecność w wyniku może pochodzić WYŁĄCZNIE z mostu — dopiero to czyni
                // test „art. 7 nie wchodzi" dowodem, a nie zbiegiem okoliczności.
                TokenCount = i == 0 ? 40 : 5,
                CharStart = 0, CharEnd = texts[i].Length,
                Embedding = new Vector(vecs[i]), EmbeddedWith = Emb.ModelId,
            });
        await db.SaveChangesAsync();
    }

    private static async Task<List<RetrievedChunk>> RetrieveMineAsync(RetrievalQuery query)
    {
        await using var db = NewDb();
        var res = await new HybridRetriever(db, Emb).RetrieveAsync(query, default);
        return res.Chunks.Where(c => c.Title.StartsWith(Source, StringComparison.Ordinal)).ToList();
    }

    private static RetrievalQuery Query(int vacatioChunks) => new()
    {
        // Pytanie użytkownika z przypadku źródłowego, w wariancie bez polskich znaków (fake embedder
        // liczy podobieństwo leksykalnie, więc liczy się nakładanie słów, nie ortografia).
        Text = "Jakie zmiany wchodza w zycie 20 wrzesnia 2026 r.",
        // Próg odcina chunki treści (TokenCount=5) od torów kandydackich — patrz SeedAsync.
        MinChunkTokens = 10,
        TopK = 5,
        VacatioLegisChunks = vacatioChunks,
    };

    [Fact] // RDZEŃ: treść wskazana w klauzuli wchodzi do wyniku, mimo że nie ma nic wspólnego z pytaniem.
    public async Task Content_referenced_by_clause_is_pulled_in()
    {
        await CleanAsync();
        await SeedAsync();

        var mine = await RetrieveMineAsync(Query(vacatioChunks: 8));

        Assert.Contains(mine, c => c.Text.Contains("wchodzi w życie"));      // klauzula trafia semantycznie
        Assert.Contains(mine, c => c.Text.Contains("kotwotronu"));            // pkt 1 lit. c — dociągnięte
        Assert.Contains(mine, c => c.Text.Contains("45 dni"));                // pkt 3 — dociągnięte
        await CleanAsync();
    }

    [Fact] // Most dociąga TYLKO wskazane jednostki — art. 7 nie jest w klauzuli, więc nie wchodzi.
    public async Task Does_not_pull_articles_absent_from_clause()
    {
        await CleanAsync();
        await SeedAsync();

        var mine = await RetrieveMineAsync(Query(vacatioChunks: 8));

        Assert.DoesNotContain(mine, c => c.Text.Contains("Przepisy przejsciowe"));
        await CleanAsync();
    }

    [Fact] // Wyłączony most = zachowanie jak przed zmianą: klauzula jest, treści nie ma. To jest
           // dokładnie zdiagnozowana porażka, więc test pilnuje, że naprawa ma realny efekt.
    public async Task Disabled_bridge_reproduces_the_original_failure()
    {
        await CleanAsync();
        await SeedAsync();

        var mine = await RetrieveMineAsync(Query(vacatioChunks: 0));

        Assert.Contains(mine, c => c.Text.Contains("wchodzi w życie"));
        Assert.DoesNotContain(mine, c => c.Text.Contains("kotwotronu"));
        await CleanAsync();
    }

    [Fact] // Limit obowiązuje — akt nowelizujący może mieć art. 1 pocięty na dziesiątki chunków,
           // a most nie ma prawa zalać kontekstu.
    public async Task Respects_chunk_limit()
    {
        await CleanAsync();
        await SeedAsync();

        var mine = await RetrieveMineAsync(Query(vacatioChunks: 1));

        Assert.Single(mine, c => c.Text.Contains("kotwotronu") || c.Text.Contains("45 dni")
            || c.Text.Contains("wstep enumeracji"));
        await CleanAsync();
    }
}
