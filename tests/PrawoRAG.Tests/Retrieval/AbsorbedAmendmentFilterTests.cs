using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Ingestion;
using PrawoRAG.Ingestion.Eli;
using PrawoRAG.Storage;
using PrawoRAG.Storage.Entities;
using PrawoRAG.Storage.Retrieval;
using PrawoRAG.Tests.Fakes;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// Wchłonięte nowelizacje poza torami semantycznymi retrievalu
/// (ANALIZA-NADGODZINY-WCHLONIETE-NOWELE-POMIAR-2026-09-01): chunki dokumentu z flagą
/// <c>AbsorbedAmendment</c> nie mogą wchodzić torem gęstym/BM25, ale jawne odwołanie do aktu
/// (lane ELI) nadal je dociąga — przepisy przejściowe noweli pozostają osiągalne na wskazanie.
/// Ten sam zestaw pilnuje zbiorczego przeliczenia flagi (RecomputeAbsorbedFlagsAsync).
/// </summary>
[Collection("LiveDb")]
public class AbsorbedAmendmentFilterTests
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

    private static async Task SeedActAsync(
        string source, string extId, string title, string text,
        bool absorbed = false, string? typedMetadataJson = null)
    {
        var vec = (await Emb.EmbedPassagesAsync([text], default))[0];
        await using var db = NewDb();
        var doc = new DocumentEntity
        {
            Id = Guid.CreateVersion7(), Source = source, ExternalId = extId, DocType = DocTypes.Act,
            Title = title, ContentHash = $"{source}:{extId}", Status = DocumentStatus.Indexed,
            InForce = true, AbsorbedAmendment = absorbed,
            TypedMetadata = typedMetadataJson is null ? null : JsonDocument.Parse(typedMetadataJson),
            IngestedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
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

    private static async Task<RetrievalResult> RetrieveAsync(string text)
    {
        await using var db = NewDb();
        return await new HybridRetriever(db, Emb)
            .RetrieveAsync(new RetrievalQuery { Text = text, MinChunkTokens = 0 }, default);
    }

    [Fact] // chunk wchłoniętej noweli NIE wchodzi ani torem gęstym (dystans 0), ani BM25
    public async Task Absorbed_amendment_is_excluded_from_semantic_lanes()
    {
        const string src = "TEST-ABSORB-1";
        await CleanAsync(src);
        const string text = "Grubloki pięćdziesiąt procent wynagrodzenia za pracę w godzinach nadliczbowych";
        await SeedActAsync(src, "DU/1996/9101", "Ustawa o zmianie ustawy - Kodeks testowy", text, absorbed: true);

        var res = await RetrieveAsync(text);
        Assert.DoesNotContain(res.Chunks, c => c.Text == text);
        await CleanAsync(src);
    }

    [Fact] // ten sam kształt dokumentu BEZ flagi wchodzi normalnie — filtr nie łapie za szeroko
    public async Task Unflagged_act_still_retrieved()
    {
        const string src = "TEST-ABSORB-2";
        await CleanAsync(src);
        const string text = "Zorbleki sto procent wynagrodzenia za pracę nadliczbową w niedziele i święta";
        await SeedActAsync(src, "DU/2003/9102", "Ustawa o zmianie ustawy - Kodeks testowy", text, absorbed: false);

        var res = await RetrieveAsync(text);
        Assert.Contains(res.Chunks, c => c.Text == text);
        await CleanAsync(src);
    }

    [Fact] // jawne odwołanie do aktu (lane ELI) dociąga nowelę MIMO flagi — przepisy przejściowe na wskazanie
    public async Task Explicit_act_reference_bypasses_filter()
    {
        const string src = "TEST-ABSORB-3";
        await CleanAsync(src);
        const string text = "Wrembloki przepis przejściowy do umów zawartych przed dniem wejścia w życie";
        await SeedActAsync("ELI", "DU/1997/9103", "Ustawa o zmianie ustawy - Kodeks testowy", text, absorbed: true);
        // Wypełniacz: retriever kończy wcześnie ([]), gdy tory semantyczne nie znajdą NIC — w pustej
        // bazie testowej lane ELI nigdy by nie wystartował (w produkcji pula semantyczna zawsze żyje).
        await SeedActAsync(src, "DU/1997/9104", "Ustawa o testowym wypełniaczu puli", "Cokolwiek przejściowego o umowach");

        try
        {
            var res = await RetrieveAsync("Co mówi przepis przejściowy w Dz.U. 1997 poz. 9103?");
            Assert.Contains(res.Chunks, c => c.Text == text);
        }
        finally
        {
            await using var db = NewDb();
            await db.Documents.Where(d => d.Source == "ELI" && d.ExternalId == "DU/1997/9103").ExecuteDeleteAsync();
        }
    }

    [Fact] // zbiorcze przeliczenie: nowela spoza list unabsorbed → true; z listy → false; akt merytoryczny → false
    public async Task Recompute_sets_flags_both_ways()
    {
        const string ext = "TEST-ABSORB-4";
        await using var db = NewDb();
        await db.Documents.Where(d => d.ContentHash.StartsWith(ext)).ExecuteDeleteAsync();

        DocumentEntity Doc(string eli, string title, string? meta = null, bool absorbed = false) => new()
        {
            Id = Guid.CreateVersion7(), Source = "ELI", ExternalId = eli, DocType = DocTypes.Act,
            Title = title, ContentHash = $"{ext}:{eli}", Status = DocumentStatus.Indexed,
            AbsorbedAmendment = absorbed,
            TypedMetadata = meta is null ? null : JsonDocument.Parse(meta),
            IngestedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };

        // Akt bazowy z NIEwchłoniętą nowelą DU/2098/2 na liście; nowele DU/2098/1 (wchłonięta,
        // startowo błędnie false) i DU/2098/2 (świeża, startowo błędnie true — test obu kierunków);
        // akt merytoryczny bez tytułu nowelizacyjnego.
        db.Documents.AddRange(
            Doc("DU/2098/100", "Ustawa Kodeks testowy",
                """{"unabsorbedAmendments":[{"EliId":"DU/2098/2","EffectiveDate":"2098-01-01"}]}"""),
            Doc("DU/2098/1", "Ustawa o zmianie ustawy - Kodeks testowy", absorbed: false),
            Doc("DU/2098/2", "Ustawa o zmianie ustawy - Kodeks testowy oraz niektórych innych ustaw", absorbed: true),
            Doc("DU/2098/3", "Ustawa o testowym podatku od zmian pogody"));
        await db.SaveChangesAsync();

        await AmendmentRelinkRunner.RecomputeAbsorbedFlagsAsync(db, default);

        // UPDATE poszedł surowym SQL-em — tracker EF trzyma stare instancje; czyścimy przed odczytem.
        db.ChangeTracker.Clear();
        var flags = await db.Documents.Where(d => d.ContentHash.StartsWith(ext))
            .ToDictionaryAsync(d => d.ExternalId, d => d.AbsorbedAmendment);
        Assert.True(flags["DU/2098/1"]);   // wchłonięta → true
        Assert.False(flags["DU/2098/2"]);  // na liście unabsorbed → false (mimo startowego true)
        Assert.False(flags["DU/2098/3"]);  // akt merytoryczny → nigdy
        Assert.False(flags["DU/2098/100"]); // akt bazowy → nigdy

        await db.Documents.Where(d => d.ContentHash.StartsWith(ext)).ExecuteDeleteAsync();
    }

    [Theory] // heurystyka tytułu — konserwatywna (wątpliwe przypadki zostają w retrievalu)
    [InlineData("Ustawa z dnia 2 lutego 1996 r. o zmianie ustawy - Kodeks pracy oraz o zmianie niektórych ustaw", true)]
    [InlineData("Ustawa o zmianie niektórych ustaw związanych z systemami wsparcia rodzin", true)]
    [InlineData("Ustawa z dnia 26 czerwca 1974 r. Kodeks pracy.", false)]
    [InlineData("Ustawa o zmianie imienia i nazwiska", false)]
    [InlineData("Ustawa o zmianie nazw niektórych szkół wyższych.", false)]
    [InlineData("Ustawa o zmianie zakresu obowiązywania Konwencji Rady Europy", false)]
    public void Amendment_title_heuristic(string title, bool expected) =>
        Assert.Equal(expected, AbsorbedAmendments.IsAmendmentTitle(title));
}
