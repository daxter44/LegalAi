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
/// T-NEIGH-LIVE (Zadanie 2 planu SAS) — dociąganie sąsiednich artykułów na żywym Postgresie.
///
/// Odtwarza przypadek źródłowy: akt, w którym trafienia semantyczne omijają właściwy przepis, bo ten
/// nazywa się inaczej. Zasiewamy ustawę, w której jeden artykuł mówi o „progu zwolnienia" i NIE
/// zawiera frazy z pytania — sprawdzamy, że wchodzi do wyniku jako sąsiad trafień.
/// </summary>
[Collection("LiveDb")]
public class ArticleNeighbourhoodLiveTests
{
    private static readonly string Conn =
        Environment.GetEnvironmentVariable("PRAWORAG_DB")
        ?? "Host=localhost;Port=5432;Database=praworag;Username=praworag;Password=praworag";

    private static PrawoRagDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PrawoRagDbContext>().UseNpgsql(Conn, o => o.UseVector()).Options);

    private static readonly FakeEmbeddingProvider Emb = new();

    private static async Task CleanAsync(string source)
    {
        await using var db = NewDb();
        await db.Documents.Where(d => d.Source == source).ExecuteDeleteAsync();
    }

    /// <summary>Zasiewa dokument o podanych chunkach (kolejność = ChunkIndex).</summary>
    private static async Task SeedAsync(string source, string docType, params string[] texts)
    {
        var vecs = await Emb.EmbedPassagesAsync(texts, default);
        await using var db = NewDb();
        var doc = new DocumentEntity
        {
            Id = Guid.CreateVersion7(), Source = source, ExternalId = "a1", DocType = docType,
            Title = $"{source} — ustawa testowa", ContentHash = $"{source}:a1",
            Status = DocumentStatus.Indexed, InForce = true,
            IngestedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(doc);
        for (var i = 0; i < texts.Length; i++)
            db.Chunks.Add(new ChunkEntity
            {
                Id = Guid.CreateVersion7(), DocumentId = doc.Id, ChunkIndex = i, Text = texts[i],
                TokenCount = 30, CharStart = 0, CharEnd = texts[i].Length,
                Embedding = new Vector(vecs[i]), EmbeddedWith = Emb.ModelId,
            });
        await db.SaveChangesAsync();
    }

    private static async Task<RetrievalResult> RetrieveAsync(RetrievalQuery query)
    {
        await using var db = NewDb();
        return await new HybridRetriever(db, Emb).RetrieveAsync(query, default);
    }

    /// <summary>
    /// Chunki należące do MOJEGO zasianego dokumentu. Baza `LiveDb` jest współdzielona z innymi
    /// testami, więc globalne liczby chunków nie są stabilnym sygnałem — asercje muszą dotyczyć
    /// wyłącznie dokumentu, który ten test zasiał.
    /// </summary>
    private static async Task<List<RetrievedChunk>> MineAsync(RetrievalQuery query, string source)
    {
        var res = await RetrieveAsync(query);
        return res.Chunks.Where(c => c.Title.StartsWith(source, StringComparison.Ordinal)).ToList();
    }

    /// <summary>
    /// Ustawa ZNACZNIE WIĘKSZA niż pobierany wycinek — tak jak w przypadku źródłowym, gdzie 8 źródeł
    /// pokrywało ~piątą część ustawy. Gdyby akt był mniejszy od TopK, cały wchodziłby do wyniku sam
    /// i sąsiedztwo nie miałoby czego dołożyć (na tym poległa pierwsza wersja tego testu).
    ///
    /// Trafienia leksykalne padną na „wplatako" (pozycje 5, 15, 25), a szukany przepis „progtako"
    /// stoi OBOK jednego z nich (pozycja 6) i frazy z pytania NIE zawiera — dokładnie jak „próg
    /// zwolnienia" obok przepisów o wpłatach.
    /// </summary>
    private static readonly string[] ActArticles = Enumerable.Range(0, 30).Select(i => i switch
    {
        5 or 15 or 25 => $"Art. {i + 1}. Wplatako regula o wplatach numer {i}.",
        6 => $"Art. {i + 1}. Progtako limit zwolnienia od podatku numer {i}.",
        _ => $"Art. {i + 1}. Postanowienia ogolne numer {i} bez zwiazku z pytaniem.",
    }).ToArray();

    [Fact] // RDZEN: przepis o innej terminologii wchodzi jako SASIAD trafien, mimo ze sam nie pasuje
           // semantycznie do pytania. To jest naprawa przypadku OKI.
    public async Task Neighbour_article_with_different_terminology_is_pulled_in()
    {
        const string src = "TEST-NEIGH-1";
        await CleanAsync(src);
        await SeedAsync(src, DocTypes.Act, ActArticles);

        var res = await RetrieveAsync(new RetrievalQuery
        {
            Text = "wplatako regula o wplatach", MinChunkTokens = 0, TopK = 20,
            NeighbourhoodRadius = 2, NeighbourhoodMinChunks = 3, NeighbourhoodTokenBudget = 20_000,
        });

        Assert.Contains(res.Chunks, c => c.Text.Contains("Progtako"));
        await CleanAsync(src);
    }

    [Fact] // Radius = 0 => zero dociagniec (test rownowaznosci).
           // Asercje liczone TYLKO na moim dokumencie: baza LiveDb jest wspoldzielona, wiec globalna
           // liczba chunkow zalezy od danych innych testow i nie jest stabilnym sygnalem.
    public async Task Zero_radius_adds_nothing()
    {
        const string src = "TEST-NEIGH-2";
        await CleanAsync(src);
        await SeedAsync(src, DocTypes.Act, ActArticles);

        var query = new RetrievalQuery
        {
            Text = "wplatako regula o wplatach", MinChunkTokens = 0, TopK = 20,
            NeighbourhoodMinChunks = 3, NeighbourhoodTokenBudget = 20_000,
        };

        var without = await MineAsync(query with { NeighbourhoodRadius = 0 }, src);
        var with = await MineAsync(query with { NeighbourhoodRadius = 2 }, src);

        Assert.True(with.Count > without.Count,
            $"sąsiedztwo miało dołożyć artykuły: bez={without.Count}, z={with.Count}");
        // Wyłączone: nic ponad to, co wygrało ranking (markerem sąsiada jest Score = MinValue).
        Assert.DoesNotContain(without, c => c.Score == double.MinValue);
        await CleanAsync(src);
    }

    [Fact] // Kolejnosc rosnaca po ChunkIndex W OBREBIE dokumentu - akt ma czytac sie liniowo.
           // Globalna lista jest sortowana po (DocumentId, ChunkIndex), wiec indeksy roznych
           // dokumentow przeplataja sie i sortowanie calej listy indeksow nie jest wlasciwa asercja.
    public async Task Result_is_ordered_by_chunk_index_within_document()
    {
        const string src = "TEST-NEIGH-3";
        await CleanAsync(src);
        await SeedAsync(src, DocTypes.Act, ActArticles);

        var mine = await MineAsync(new RetrievalQuery
        {
            Text = "wplatako regula o wplatach", MinChunkTokens = 0, TopK = 20,
            NeighbourhoodRadius = 2, NeighbourhoodMinChunks = 3, NeighbourhoodTokenBudget = 20_000,
        }, src);

        var indexes = mine.Select(c => c.ChunkIndex).ToList();
        Assert.Equal(indexes.OrderBy(x => x), indexes);
        await CleanAsync(src);
    }

    [Fact] // BUDZET TOKENOW to cala obsluga przypadku "kodeks": maly budzet => mniej sasiadow.
    public async Task Token_budget_limits_expansion()
    {
        const string src = "TEST-NEIGH-4";
        await CleanAsync(src);
        await SeedAsync(src, DocTypes.Act, ActArticles);

        var query = new RetrievalQuery
        {
            Text = "wplatako regula o wplatach", MinChunkTokens = 0, TopK = 20,
            NeighbourhoodRadius = 3, NeighbourhoodMinChunks = 3,
        };

        var big = await RetrieveAsync(query with { NeighbourhoodTokenBudget = 20_000 });
        var tiny = await RetrieveAsync(query with { NeighbourhoodTokenBudget = 1 });

        Assert.True(tiny.Chunks.Count < big.Chunks.Count);
        await CleanAsync(src);
    }

    [Fact] // ORZECZENIA nie sa rozszerzane, nawet gdy zdominuja wynik.
    public async Task Judgments_are_not_expanded()
    {
        const string src = "TEST-NEIGH-5";
        await CleanAsync(src);
        await SeedAsync(src, DocTypes.Judgment,
            "Sekcja jedna orzeczenia iotako.", "Sekcja druga orzeczenia iotako.",
            "Sekcja trzecia orzeczenia iotako.", "Sekcja czwarta kappatako niezwiazana.");

        var query = new RetrievalQuery
        {
            Text = "iotako orzeczenie sekcja", MinChunkTokens = 0, TopK = 20,
            NeighbourhoodMinChunks = 3, NeighbourhoodTokenBudget = 20_000,
        };

        var without = await MineAsync(query with { NeighbourhoodRadius = 0 }, src);
        var with = await MineAsync(query with { NeighbourhoodRadius = 3 }, src);

        Assert.Equal(without.Count, with.Count);
        Assert.DoesNotContain(with, c => c.Score == double.MinValue);   // brak markera sąsiada
        await CleanAsync(src);
    }

    [Fact] // Sasiedztwo NIE podnosi ExactMatchHits - bramka abstynencji stoi na jawnym asku
           // uzytkownika, a dociagniety kontekst jest sygnalem POCHODNYM (jak most cytowan).
    public async Task Neighbours_do_not_inflate_exact_match_signal()
    {
        const string src = "TEST-NEIGH-6";
        await CleanAsync(src);
        await SeedAsync(src, DocTypes.Act, ActArticles);

        var res = await RetrieveAsync(new RetrievalQuery
        {
            Text = "wplatako regula o wplatach", MinChunkTokens = 0, TopK = 20,
            NeighbourhoodRadius = 2, NeighbourhoodMinChunks = 3, NeighbourhoodTokenBudget = 20_000,
        });

        Assert.Equal(0, res.ExactMatchHits);
        await CleanAsync(src);
    }
}
