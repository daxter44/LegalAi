using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain.Embeddings;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Storage;

namespace PrawoRAG.Ingestion;

/// <summary>
/// ODP-2: run przerwany przez bezpiecznik — seria porażek z rzędu wskazuje awarię infrastruktury
/// (TEI/DB/sieć), nie złe dokumenty. Po naprawie przyczyny ten sam run wznawia się tanio (fast-skip),
/// a dokumenty Failed z serii są przetwarzane od nowa (nie ma ich w zbiorze pomijania).
/// </summary>
public sealed class ProcessAbortedException(string message) : Exception(message);

/// <summary>
/// Faza B — przetwarza surowe dokumenty z LOKALNEGO magazynu przez <see cref="IIngestionPipeline"/>.
/// Działa OFFLINE (zero hitów do źródła): to ścieżka re-processingu po zmianie normalizera/chunkera/modelu.
/// Każdy dokument w osobnym scope (świeży DbContext per dokument — brak narastania trackingu).
/// Odporność (plan ODP): fast-skip czyni wznowienie po awarii tanim (minuty zamiast godzin).
/// </summary>
public sealed class RawProcessRunner(
    IServiceScopeFactory scopeFactory,
    IRawDocumentStore store,
    IOptions<ProcessOptions> options,
    ILogger<RawProcessRunner> log)
{
    public async Task<IngestSummary> RunAsync(string source, int? maxItems, CancellationToken ct)
    {
        var skipSet = ProcessSkipSet.Empty;
        if (options.Value.FastSkip)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();
            var embedder = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
            skipSet = await ProcessSkipSet.LoadAsync(db, source, embedder.ModelId, ct);
            log.LogInformation(
                "Fast-skip {Source}: {N} dokumentów już zaindeksowanych (model {Model}) — pominięcia bez zapytań do bazy",
                source, skipSet.Count, embedder.ModelId);
        }
        return await RunAsync(source, maxItems, skipSet, ct);
    }

    /// <summary>Rdzeń pętli z jawnym zbiorem fast-skip — wariant dla testów jednostkowych (bez DB).</summary>
    public async Task<IngestSummary> RunAsync(string source, int? maxItems, ProcessSkipSet skipSet, CancellationToken ct)
    {
        int inserted = 0, updated = 0, skipped = 0, reembedded = 0, failed = 0, processed = 0;
        var failStreak = 0; // ODP-2: porażki Z RZĘDU — zerowane każdym innym wynikiem (także fast-skipem)
        FailureReport? report = null; // ODP-3: leniwie przy pierwszej porażce
        var lastId = "(brak)";
        log.LogInformation("Process {Source} start (z magazynu surowych)", source);

        await foreach (var raw in store.EnumerateAsync(source, ct))
        {
            if (maxItems is { } max && processed >= max) break;
            processed++;
            lastId = raw.ExternalId;

            // ODP-1: pominięcie bez scope'a i bez roundtripu do bazy — semantyka jak w pipeline
            // (ten sam Hashing.Sha256Hex; zbiór zawiera tylko Indexed + bieżący model embeddingu).
            if (skipSet.Contains(raw.ExternalId, Hashing.Sha256Hex(raw.RawContent)))
            {
                skipped++;
                failStreak = 0;
                LogProgress();
                continue;
            }

            using var docScope = scopeFactory.CreateScope();
            var pipeline = docScope.ServiceProvider.GetRequiredService<IIngestionPipeline>();
            var result = await pipeline.ProcessAsync(raw, ct);
            switch (result.Outcome)
            {
                case IngestOutcome.Inserted: inserted++; break;
                case IngestOutcome.Updated: updated++; break;
                case IngestOutcome.Skipped: skipped++; break;
                case IngestOutcome.ReEmbedded: reembedded++; break;
                case IngestOutcome.Failed: failed++; break;
            }

            if (result.Outcome == IngestOutcome.Failed)
            {
                failStreak++;
                // ODP-3: pozycja + tożsamość + etap w logu; pełny wyjątek w raporcie JSONL.
                log.LogWarning("Porażka #{Seq}: {Source}/{Id} [{Stage}] {Error}",
                    processed, source, raw.ExternalId, result.FailureStage,
                    result.Error?.GetBaseException().Message);
                report ??= FailureReport.Create(options.Value.FailureLogDir, source);
                report.Write(processed, raw, result.FailureStage, result.Error);

                var limit = options.Value.FailStreakLimit;
                if (limit > 0 && failStreak >= limit)
                {
                    var soFar = new IngestSummary(inserted, updated, skipped, reembedded, failed);
                    log.LogCritical(
                        "Bezpiecznik: {Streak} porażek z rzędu (ostatnia: #{Seq} {Source}/{Id}, etap {Stage}) — to wygląda " +
                        "na awarię infrastruktury (TEI/DB/sieć), nie na złe dokumenty. Dotychczas: {Summary}. Pełne błędy: " +
                        "{Report}. Napraw przyczynę i uruchom ponownie: fast-skip przewinie gotowe, a dokumenty Failed " +
                        "z tej serii przetworzą się od nowa.",
                        failStreak, processed, source, raw.ExternalId, result.FailureStage, soFar, report.FilePath);
                    throw new ProcessAbortedException(
                        $"Przerwano po {failStreak} porażkach z rzędu (ostatnia: {source}/{raw.ExternalId}, pozycja #{processed}, " +
                        $"etap {result.FailureStage}). Pełne błędy: {report.FilePath}. Próg: Ingestion:FailStreakLimit={limit} (0 wyłącza).");
                }
            }
            else
            {
                failStreak = 0;
            }
            LogProgress();
        }

        var summary = new IngestSummary(inserted, updated, skipped, reembedded, failed);
        log.LogInformation("Process {Source} koniec: {Summary}", source, summary);
        if (failed > 0 && report is not null)
            log.LogWarning(
                "Run z {Failed} porażkami. Pełne błędy: {Report}. Podgląd w DB: SELECT \"ExternalId\", \"FailureReason\", " +
                "\"AttemptCount\" FROM documents WHERE \"Status\" = {FailedStatus} AND \"Source\" = '{Source}';",
                failed, report.FilePath, (int)Domain.DocumentStatus.Failed, source);
        return summary;

        void LogProgress()
        {
            if (processed % 50 == 0)
                log.LogInformation("Postęp process {Source}: ins={I} upd={U} skip={S} reemb={R} fail={F} (ostatni: {LastId})",
                    source, inserted, updated, skipped, reembedded, failed, lastId);
        }
    }
}
