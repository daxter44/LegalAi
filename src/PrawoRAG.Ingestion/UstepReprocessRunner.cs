using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Ingestion.Eli;
using PrawoRAG.Storage;

namespace PrawoRAG.Ingestion;

public sealed record UstepReprocessSummary(int Targets, int Done, int Skipped, int Failed)
{
    public override string ToString() => $"targets={Targets} done={Done} skipped={Skipped} failed={Failed}";
}

/// <summary>
/// Wymuszony reprocessing ustaw pod podział na ustępy (`unit_pass` w ActNormalizer — diagnoza
/// 2026-08-31: 85% ustaw szło do bazy całymi artykułami, rozmyte embeddingi; pilot na
/// DU/2001/733: art. 11 z #512 → #8 i wchodzi do kandydatów). Sekwencja per akt, wzorem pilota:
/// backup chunków → Status='Fetched' (omija skip po content-hash) → świeży fetch z ELI
/// (konektor sam wybiera najnowszy tekst jednolity) → pełny pipeline (normalize+chunk+embed,
/// transakcyjna podmiana chunków).
///
/// CEL dobierany automatycznie: akty ELI z torem HTML (kotwice w lokatorach), które mają długi
/// (≥400 tok) chunk artykułowy BEZ ustępu — czyli dokładnie populacja z pomiaru w diagnozie.
/// Akty toru PDF (bez kotwic) świadomie POZA zakresem — parser PDF nie dzieli ustępów (osobna
/// decyzja). Akt, którego HTML faktycznie nie ma ustępów, przetworzy się do identycznej postaci
/// (koszt: jeden zbędny embed) — nieszkodliwe.
///
/// WZNAWIALNOŚĆ: plik checkpoint (jeden ExternalId per linia) — przerwany run wznawia się od
/// miejsca przerwania; backup ma ochronę przed duplikatami przy wznowieniu po crashu w połowie aktu.
/// </summary>
public sealed class UstepReprocessRunner(
    IServiceScopeFactory scopeFactory,
    ILogger<UstepReprocessRunner> log)
{
    public async Task<UstepReprocessSummary> RunAsync(
        string checkpointFile, int? maxItems, int delayMs, CancellationToken ct)
    {
        // 1) Lista celów — raz, na starcie (kolejność stabilna po ExternalId; wznowienie = checkpoint).
        List<string> targets;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();
            db.Database.SetCommandTimeout(TimeSpan.FromMinutes(20));
            targets = await db.Database.SqlQuery<string>($"""
                SELECT DISTINCT d."ExternalId" AS "Value"
                FROM documents d
                WHERE d."Source" = 'ELI' AND d."DocType" = 'act'
                  AND EXISTS (SELECT 1 FROM chunks c WHERE c."DocumentId" = d."Id"
                              AND c."ArticleNo" IS NOT NULL
                              AND c."Locator"->>'Paragraph' IS NULL
                              AND c."Locator"->>'Anchor' IS NOT NULL
                              AND c."TokenCount" >= 400)
                ORDER BY 1
                """).ToListAsync(ct);

            // Tabela backupu (rollback per akt bez ingestii) — raz.
            await db.Database.ExecuteSqlRawAsync(
                """CREATE TABLE IF NOT EXISTS reprocess_ustepy_backup AS SELECT * FROM chunks WHERE false""", ct);
        }

        var done = File.Exists(checkpointFile)
            ? new HashSet<string>(await File.ReadAllLinesAsync(checkpointFile, ct), StringComparer.OrdinalIgnoreCase)
            : [];
        log.LogInformation("Reprocess-ustepy: celów {Targets}, w checkpoincie {Done}.", targets.Count, done.Count);

        int ok = 0, skipped = 0, failed = 0, processed = 0;
        foreach (var externalId in targets)
        {
            ct.ThrowIfCancellationRequested();
            if (done.Contains(externalId)) { skipped++; continue; }
            if (maxItems is { } cap && processed >= cap) break;
            processed++;

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();
            var connector = scope.ServiceProvider.GetServices<ISourceConnector>()
                .OfType<EliSejmConnector>().FirstOrDefault()
                ?? throw new InvalidOperationException("Brak konektora ELI.");
            var pipeline = scope.ServiceProvider.GetRequiredService<IIngestionPipeline>();

            try
            {
                var doc = await db.Documents.FirstOrDefaultAsync(
                    d => d.Source == SourceKeys.Eli && d.ExternalId == externalId, ct);
                if (doc is null) { failed++; log.LogWarning("{Id}: brak dokumentu w bazie — pomijam.", externalId); continue; }

                // Backup chunków PRZED podmianą (ochrona przed duplikatami przy wznowieniu po crashu).
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO reprocess_ustepy_backup
                    SELECT c.* FROM chunks c
                    WHERE c."DocumentId" = {doc.Id}
                      AND NOT EXISTS (SELECT 1 FROM reprocess_ustepy_backup b WHERE b."Id" = c."Id")
                    """, ct);

                // Wymuszenie pełnego przetworzenia: skip w pipeline wymaga Status=Indexed.
                doc.Status = DocumentStatus.Fetched;
                await db.SaveChangesAsync(ct);

                var raw = await connector.FetchOneAsync(externalId, ct);
                if (raw is null)
                {
                    failed++;
                    log.LogWarning("{Id}: fetch z ELI nie powiódł się — akt zostaje ze Status=Fetched (dokończy go kolejny run).", externalId);
                    continue;
                }

                var result = await pipeline.ProcessAsync(raw, ct);
                if (result.Outcome is IngestOutcome.Failed)
                {
                    failed++;
                    log.LogWarning("{Id}: pipeline Failed na etapie {Stage}.", externalId, result.FailureStage);
                    continue;
                }

                ok++;
                await File.AppendAllTextAsync(checkpointFile, externalId + Environment.NewLine, ct);
                if (ok % 25 == 0)
                    log.LogInformation("Reprocess-ustepy postęp: done={Ok} failed={Failed} / targets={Targets}.",
                        ok, failed, targets.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                log.LogError(ex, "{Id}: nieoczekiwany błąd — kontynuuję od następnego aktu.", externalId);
            }

            if (delayMs > 0) await Task.Delay(delayMs, ct); // grzeczność wobec api.sejm.gov.pl
        }

        return new UstepReprocessSummary(targets.Count, ok, skipped, failed);
    }
}
