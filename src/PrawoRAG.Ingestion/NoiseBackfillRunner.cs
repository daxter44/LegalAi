using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pgvector;
using PrawoRAG.Domain.Embeddings;
using PrawoRAG.Ingestion.Cleaning;
using PrawoRAG.Storage;
using PrawoRAG.Storage.Entities;

namespace PrawoRAG.Ingestion;

public sealed record NoiseBackfillSummary(string Problem, int Scanned, int Cleaned, int Unchanged, int Skipped)
{
    public override string ToString() =>
        $"[{Problem}] scanned={Scanned} cleaned={Cleaned} unchanged={Unchanged} skipped={Skipped}";
}

/// <summary>
/// Backfill jakości treści chunków — trzy zmierzone źródła szumu (PLAN-NAPRAWA-SZUMU-CHUNKOW-2026-08-28.md):
/// <c>mojibake</c> (stare PDF-y Dz.U., bajty Mac-CE czytane jako MacRoman), <c>footnotes</c>
/// (bibliograficzna historia nowelizacji dominująca chunk), <c>bullets</c> (markery list „⚫" z HTML SAOS).
/// Dla każdego dotkniętego chunka: czyszczenie tekstu → przeliczenie TokenCount → ponowny embedding →
/// UPDATE (SearchVector jako kolumna generowana przelicza się sam). Przed każdą partią pełna kopia
/// wierszy do <c>chunk_noise_backup</c> (rollback bez ponownej ingestii).
///
/// Paginacja keysetem po Id (nie „od początku po sygnaturze") — gwarantuje postęp także wtedy, gdy
/// czyszczenie jakiegoś chunka nic nie zmienia (np. pozycje „poz. N" rozproszone w treści normatywnej,
/// nie w jednym ciągu — celowo NIE ruszane). Selekcja jest samowygaszająca: oczyszczony tekst nie
/// spełnia już warunku selekcji, więc przerwany run można bezpiecznie wznowić od zera.
/// </summary>
public sealed class NoiseBackfillRunner(
    IServiceScopeFactory scopeFactory,
    IEmbeddingProvider embedder,
    ILogger<NoiseBackfillRunner> log)
{
    /// <summary>Chunk krótszy po czyszczeniu niż ten próg = czyszczenie zjadło całą treść — zostawiamy oryginał.</summary>
    private const int MinCleanedLength = 20;

    private sealed record ProblemSpec(string Name, string SelectionSql, Func<string, string> Clean);

    private static readonly ProblemSpec[] Specs =
    [
        new("mojibake",
            """SELECT c.* FROM chunks c WHERE c."Text" ~ '[∏Ê˝ƒ]' AND c."Id" > {0} ORDER BY c."Id" LIMIT {1}""",
            MojibakeTranscoder.FixIfAffected),
        new("footnotes",
            """
            SELECT c.* FROM chunks c JOIN documents d ON d."Id" = c."DocumentId"
            WHERE d."DocType" = 'act'
              AND (SELECT count(*) FROM regexp_matches(c."Text", 'poz\.\s*\d+', 'g')) >= 5
              AND c."Id" > {0} ORDER BY c."Id" LIMIT {1}
            """,
            AmendmentFootnoteCleaner.Clean),
        new("bullets",
            """SELECT c.* FROM chunks c WHERE c."Text" ~ '[⚫●•▪◦⬤]' AND c."Id" > {0} ORDER BY c."Id" LIMIT {1}""",
            BulletCleaner.Clean),
    ];

    public async Task<IReadOnlyList<NoiseBackfillSummary>> RunAsync(
        IReadOnlyCollection<string> problems, bool dryRun, int batchSize, int? maxChunks, CancellationToken ct)
    {
        var summaries = new List<NoiseBackfillSummary>();
        foreach (var spec in Specs)
        {
            if (!problems.Contains(spec.Name, StringComparer.OrdinalIgnoreCase)) continue;
            summaries.Add(await RunProblemAsync(spec, dryRun, batchSize, maxChunks, ct));
        }
        return summaries;
    }

    private async Task<NoiseBackfillSummary> RunProblemAsync(
        ProblemSpec spec, bool dryRun, int batchSize, int? maxChunks, CancellationToken ct)
    {
        int scanned = 0, cleaned = 0, unchanged = 0, skipped = 0;
        var lastId = Guid.Empty;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (maxChunks is { } cap && scanned >= cap) break;

            // Świeży scope per partia — DbContext bez narastającego change trackera na dziesiątkach tysięcy wierszy.
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();
            // Selekcja z regexp/regexp_matches skanuje sekwencyjnie coraz dalej, im mniej dopasowań zostało —
            // pod koniec przebiegu pojedyncze zapytanie przekracza domyślne 30 s (zmierzone: crash przy
            // footnotes scanned=14400/14726). Backfill to narzędzie offline — długi timeout jest właściwy.
            db.Database.SetCommandTimeout(TimeSpan.FromMinutes(20));

            var batch = await db.Chunks
                .FromSqlRaw(spec.SelectionSql, lastId, batchSize)
                .AsTracking()
                .ToListAsync(ct);
            log.LogDebug("[{Problem}] partia: {Count} wierszy (od {LastId}).", spec.Name, batch.Count, lastId);
            if (batch.Count == 0) break;

            lastId = batch[^1].Id;
            scanned += batch.Count;

            var toUpdate = new List<(ChunkEntity Chunk, string NewText)>();
            foreach (var chunk in batch)
            {
                var newText = spec.Clean(chunk.Text);
                if (newText == chunk.Text) { unchanged++; continue; }
                if (newText.Trim().Length < MinCleanedLength)
                {
                    // Czyszczenie zjadło praktycznie całość (chunk był samym szumem) — nie podmieniamy,
                    // żeby nie zostawić pustego wektora; kandydat do ręcznego przeglądu.
                    skipped++;
                    log.LogWarning("[{Problem}] chunk {Id}: po czyszczeniu {Len} znaków — pomijam.",
                        spec.Name, chunk.Id, newText.Trim().Length);
                    continue;
                }
                toUpdate.Add((chunk, newText));
            }

            if (dryRun)
            {
                foreach (var (chunk, newText) in toUpdate.Take(3))
                    log.LogInformation("[{Problem}] DRY-RUN {Id}:\n--- PRZED ---\n{Before}\n--- PO ---\n{After}",
                        spec.Name, chunk.Id, Truncate(chunk.Text), Truncate(newText));
                cleaned += toUpdate.Count;
                continue;
            }

            if (toUpdate.Count > 0)
            {
                await BackupAsync(db, spec.Name, toUpdate.Select(t => t.Chunk.Id).ToArray(), ct);

                var texts = toUpdate.Select(t => t.NewText).ToList();
                var tokenCounts = await embedder.CountTokensAsync(texts, ct);
                var vectors = await embedder.EmbedPassagesAsync(texts, ct);

                for (var i = 0; i < toUpdate.Count; i++)
                {
                    var (chunk, newText) = toUpdate[i];
                    chunk.Text = newText;
                    chunk.TokenCount = tokenCounts[i];
                    chunk.Embedding = new Vector(vectors[i]);
                    chunk.EmbeddedWith = embedder.ModelId;
                }
                await db.SaveChangesAsync(ct);
                cleaned += toUpdate.Count;
            }

            log.LogInformation("[{Problem}] postęp: scanned={Scanned} cleaned={Cleaned} unchanged={Unchanged} skipped={Skipped}",
                spec.Name, scanned, cleaned, unchanged, skipped);
        }

        return new NoiseBackfillSummary(spec.Name, scanned, cleaned, unchanged, skipped);
    }

    /// <summary>Kopia oryginałów do <c>chunk_noise_backup</c> (idempotentna — ON CONFLICT DO NOTHING:
    /// przy wznowieniu runu zostaje NAJSTARSZA, czyli oryginalna wersja).</summary>
    private static async Task BackupAsync(PrawoRagDbContext db, string problem, Guid[] ids, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS chunk_noise_backup (
                "Id" uuid PRIMARY KEY,
                "Text" text NOT NULL,
                "TokenCount" int NOT NULL,
                "Embedding" vector(1024),
                "EmbeddedWith" text,
                "Problem" text NOT NULL,
                "BackedUpAt" timestamptz NOT NULL DEFAULT now())
            """, ct);
        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO chunk_noise_backup ("Id","Text","TokenCount","Embedding","EmbeddedWith","Problem")
             SELECT "Id","Text","TokenCount","Embedding","EmbeddedWith",{problem} FROM chunks WHERE "Id" = ANY({ids})
             ON CONFLICT ("Id") DO NOTHING
             """, ct);
    }

    private static string Truncate(string s) => s.Length <= 600 ? s : s[..600] + " […]";
}
