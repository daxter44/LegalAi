using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Documents;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Storage;
using PrawoRAG.Storage.Entities;
using PrawoRAG.Storage.Retrieval;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// T-AKT-AUG — PRAWDZIWY <see cref="TemporalAugmenter"/> na żywym Postgresie.
///
/// Dlaczego ten plik powstał: augmenter był dotąd pokryty wyłącznie atrapami (`NoOpAugmenter`
/// i przeciążki w testach czatu), a KAŻDY jego wywołujący owija go w
/// `try { … } catch { /* best-effort */ }` (ChatService, /api/chat, eval odmów). To znaczy, że dowolny
/// błąd w środku — nieprzetłumaczalne LINQ, zmiana kształtu metadanych, literówka w kluczu jsonb —
/// jest CICHY: oznaczanie i dokładanie nowel po prostu przestaje działać, a odpowiedź wygląda normalnie.
/// Kombinacja „zero testów + połknięty wyjątek" to najgorszy możliwy wariant, więc niezmienniki
/// augmentera muszą być sprawdzane na realnej bazie, nie na atrapie.
///
/// Kształt metadanych mirroruje produkcję: `AmendmentRef` jest serializowany domyślnymi opcjami
/// (PascalCase `EliId`/`EffectiveDate`), a `ParseUnabsorbed` czyta case-sensitive — test zasiewa
/// dokładnie ten zapis, żeby nie dowodzić czegoś innego niż działa na produkcji.
/// </summary>
[Collection("LiveDb")]
public class TemporalAugmenterTests
{
    private const string Src = "TEST-AKT-AUG";
    private const string BaseActExtId = "DU/2099/100";
    private const string AmendmentExtId = "DU/2099/101";
    private const string EffectiveDate = "2099-03-01";

    /// <summary>Fragment noweli w języku diffu ZTP — wzmianka artykułu + czasownik nowelizacyjny,
    /// czego wymaga <see cref="AmendmentDiffMatcher.MentionsArticleChange"/>.</summary>
    private const string AmendmentChunkText =
        "W ustawie o testowaniu wprowadza się następujące zmiany: w art. 94 § 2 otrzymuje brzmienie: " +
        "„termin wynosi 30 dni od dnia doręczenia zawiadomienia\".";

    private static readonly string Conn =
        Environment.GetEnvironmentVariable("PRAWORAG_DB")
        ?? "Host=localhost;Port=5432;Database=praworag;Username=praworag;Password=praworag";

    private static PrawoRagDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PrawoRagDbContext>().UseNpgsql(Conn, o => o.UseVector()).Options);

    private static async Task CleanAsync()
    {
        await using var db = NewDb();
        await db.Documents.Where(d => d.Source == Src).ExecuteDeleteAsync();
    }

    private static async Task<(Guid BaseDocId, Guid AmendmentDocId)> SeedAsync()
    {
        await using var db = NewDb();

        // Akt bazowy: w metadanych nowela NIEWCHŁONIĘTA do tekstu jednolitego.
        var unabsorbed = new[] { new AmendmentRef(AmendmentExtId, EffectiveDate) };
        var baseDoc = NewDoc(BaseActExtId, JsonSerializer.SerializeToDocument(
            new Dictionary<string, object?> { ["unabsorbedAmendments"] = unabsorbed }));
        db.Documents.Add(baseDoc);
        db.Chunks.Add(NewChunk(baseDoc.Id, "Art. 94 § 2. Termin wynosi 14 dni.", article: "94"));

        // Nowela jako osobny dokument-akt + jej chunk z językiem diffu.
        var amendmentDoc = NewDoc(AmendmentExtId, typedMetadata: null);
        db.Documents.Add(amendmentDoc);
        db.Chunks.Add(NewChunk(amendmentDoc.Id, AmendmentChunkText, article: null));

        await db.SaveChangesAsync();
        return (baseDoc.Id, amendmentDoc.Id);
    }

    private static DocumentEntity NewDoc(string extId, JsonDocument? typedMetadata) => new()
    {
        Id = Guid.CreateVersion7(), Source = Src, ExternalId = extId, DocType = DocTypes.Act,
        Title = $"{Src}/{extId}", ContentHash = $"{Src}:{extId}", Status = DocumentStatus.Indexed,
        InForce = true, TypedMetadata = typedMetadata,
        IngestedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static ChunkEntity NewChunk(Guid docId, string text, string? article) => new()
    {
        Id = Guid.CreateVersion7(), DocumentId = docId, ChunkIndex = 0, Text = text,
        TokenCount = 30, CharStart = 0, CharEnd = text.Length, ArticleNo = article,
        Locator = JsonSerializer.SerializeToDocument(new CitationLocator { Article = article }),
    };

    private static RetrievedChunk ActChunk(Guid docId, string text, string? article) => new()
    {
        ChunkId = Guid.CreateVersion7(), DocumentId = docId, Text = text, Source = Src,
        DocType = DocTypes.Act, Title = "akt", Score = 0.9, Similarity = 0.7,
        Locator = article is null ? null : new CitationLocator { Article = article },
    };

    [Fact] // AUG1: nowela dotycząca PYTANEGO artykułu jest dokładana i oznaczona datą wejścia w życie
    public async Task Adds_amendment_fragment_for_article_in_results()
    {
        await CleanAsync();
        try
        {
            var (baseDocId, _) = await SeedAsync();

            await using var db = NewDb();
            var retrieved = new[] { ActChunk(baseDocId, "Art. 94 § 2. Termin wynosi 14 dni.", "94") };
            var result = await new TemporalAugmenter(db).AugmentAsync(
                new RetrievalQuery { Text = "jaki jest termin z art. 94 § 2?" }, retrieved, default);

            // Kontrakt: NIGDY nie usuwa wejścia + dokłada fragment noweli.
            Assert.Contains(result, c => c.Text.Contains("Termin wynosi 14 dni"));
            var added = Assert.Single(result, c => c.Text.Contains("otrzymuje brzmienie"));
            Assert.Equal(EffectiveDate, added.AmendmentEffectiveDate);
            Assert.Contains("[NOWELIZACJA", added.Text); // marker czytany przez regułę 6 promptu
        }
        finally { await CleanAsync(); }
    }

    [Fact] // AUG2: chunk, którego WŁASNY dokument jest niewchłoniętą nowelą, dostaje oznaczenie (AKT-4b)
    public async Task Tags_chunk_whose_own_document_is_an_unabsorbed_amendment()
    {
        await CleanAsync();
        try
        {
            var (_, amendmentDocId) = await SeedAsync();

            await using var db = NewDb();
            // Nowela trafiła do wyników ZWYKŁYM retrievalem (pytanie sparafrazowane blisko jej treści),
            // nie przez dopasowanie cytatu — oznaczenie ma się pojawić i tak.
            var retrieved = new[] { ActChunk(amendmentDocId, AmendmentChunkText, article: null) };
            var result = await new TemporalAugmenter(db).AugmentAsync(
                new RetrievalQuery { Text = "od kiedy nowy termin?" }, retrieved, default);

            var tagged = Assert.Single(result);
            Assert.Equal(EffectiveDate, tagged.AmendmentEffectiveDate);
        }
        finally { await CleanAsync(); }
    }

    [Fact] // AUG3: brak chunków aktu w wynikach → augmenter oddaje wejście bez zmian (i bez zapytań)
    public async Task Returns_input_untouched_when_no_act_chunks()
    {
        await using var db = NewDb();
        var retrieved = new[]
        {
            new RetrievedChunk
            {
                ChunkId = Guid.CreateVersion7(), DocumentId = Guid.CreateVersion7(), Text = "wyrok",
                Source = "SAOS", DocType = DocTypes.Judgment, Title = "wyrok", Score = 0.5,
            },
        };

        var result = await new TemporalAugmenter(db).AugmentAsync(
            new RetrievalQuery { Text = "cokolwiek" }, retrieved, default);

        Assert.Same(retrieved, result);
    }
}
