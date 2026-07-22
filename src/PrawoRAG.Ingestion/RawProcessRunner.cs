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

    /// <summary>Rdzeń pętli z jawnym zbiorem fast-skip — wariant dla testów jednostkowych (bez DB).
    /// RÓWN-1: przetwarzanie równoległe (<see cref="ProcessOptions.ProcessParallelism"/>); przy stopniu 1
    /// wykonanie jest sekwencyjne i uporządkowane — identyczne z poprzednim `await foreach`.</summary>
    public async Task<IngestSummary> RunAsync(string source, int? maxItems, ProcessSkipSet skipSet, CancellationToken ct)
    {
        long inserted = 0, updated = 0, skipped = 0, reembedded = 0, failed = 0, processed = 0;
        var failStreak = 0; // ODP-2: porażki Z RZĘDU — zerowane każdym innym wynikiem (także fast-skipem)
        FailureReport? report = null; // ODP-3: leniwie przy pierwszej porażce
        var lastId = "(brak)";
        var stateLock = new object(); // failStreak + abort + leniwy report (sekcja krytyczna przy >1 wątku)
        ProcessAbortedException? abort = null;

        var parallelism = Math.Max(1, options.Value.ProcessParallelism);
        log.LogInformation("Process {Source} start (z magazynu surowych, parallelism={P})", source, parallelism);

        // Bezpiecznik ODP-2 przerywa run przez ANULOWANIE (a nie wyjątek z ciała) — czysto kończy
        // pozostałe wątki; właściwy ProcessAbortedException rzucamy po pętli.
        using var abortCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var parallelOpts = new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelism,
            CancellationToken = abortCts.Token,
        };

        try
        {
            await Parallel.ForEachAsync(Bounded(store.EnumerateAsync(source, abortCts.Token), maxItems, abortCts.Token),
                parallelOpts, async (raw, itemCt) =>
            {
                var seq = (int)Interlocked.Increment(ref processed);
                lastId = raw.ExternalId; // best-effort (tylko log) — przy >1 wątku bywa nieuporządkowany

                // ODP-1: pominięcie bez scope'a i bez roundtripu do bazy — semantyka jak w pipeline
                // (ten sam Hashing.Sha256Hex; zbiór zawiera tylko Indexed + bieżący model embeddingu).
                if (skipSet.Contains(raw.ExternalId, Hashing.Sha256Hex(raw.RawContent)))
                {
                    Interlocked.Increment(ref skipped);
                    lock (stateLock) failStreak = 0;
                    LogProgress(seq);
                    return;
                }

                using var docScope = scopeFactory.CreateScope();
                var pipeline = docScope.ServiceProvider.GetRequiredService<IIngestionPipeline>();
                var result = await pipeline.ProcessAsync(raw, itemCt);
                switch (result.Outcome)
                {
                    case IngestOutcome.Inserted: Interlocked.Increment(ref inserted); break;
                    case IngestOutcome.Updated: Interlocked.Increment(ref updated); break;
                    case IngestOutcome.Skipped: Interlocked.Increment(ref skipped); break;
                    case IngestOutcome.ReEmbedded: Interlocked.Increment(ref reembedded); break;
                    case IngestOutcome.Failed: Interlocked.Increment(ref failed); break;
                }

                if (result.Outcome == IngestOutcome.Failed)
                {
                    // ODP-3: pozycja + tożsamość + etap w logu; pełny wyjątek w raporcie JSONL.
                    log.LogWarning("Porażka #{Seq}: {Source}/{Id} [{Stage}] {Error}",
                        seq, source, raw.ExternalId, result.FailureStage,
                        result.Error?.GetBaseException().Message);

                    var limit = options.Value.FailStreakLimit;
                    lock (stateLock)
                    {
                        (report ??= FailureReport.Create(options.Value.FailureLogDir, source))
                            .Write(seq, raw, result.FailureStage, result.Error);
                        failStreak++;
                        if (limit > 0 && failStreak >= limit && abort is null)
                        {
                            var soFar = new IngestSummary((int)inserted, (int)updated, (int)skipped, (int)reembedded, (int)failed);
                            log.LogCritical(
                                "Bezpiecznik: {Streak} porażek z rzędu (ostatnia: #{Seq} {Source}/{Id}, etap {Stage}) — to wygląda " +
                                "na awarię infrastruktury (TEI/DB/sieć), nie na złe dokumenty. Dotychczas: {Summary}. Pełne błędy: " +
                                "{Report}. Napraw przyczynę i uruchom ponownie: fast-skip przewinie gotowe, a dokumenty Failed " +
                                "z tej serii przetworzą się od nowa.",
                                failStreak, seq, source, raw.ExternalId, result.FailureStage, soFar, report.FilePath);
                            abort = new ProcessAbortedException(
                                $"Przerwano po {failStreak} porażkach z rzędu (ostatnia: {source}/{raw.ExternalId}, pozycja #{seq}, " +
                                $"etap {result.FailureStage}). Pełne błędy: {report.FilePath}. Próg: Ingestion:FailStreakLimit={limit} (0 wyłącza).");
                            abortCts.Cancel();
                        }
                    }
                }
                else
                {
                    lock (stateLock) failStreak = 0;
                }
                LogProgress(seq);
            });
        }
        catch (OperationCanceledException) when (abort is not null)
        {
            throw abort; // bezpiecznik ODP-2 — anulowanie było celowe, surfacujemy właściwy wyjątek
        }

        var summary = new IngestSummary((int)inserted, (int)updated, (int)skipped, (int)reembedded, (int)failed);
        log.LogInformation("Process {Source} koniec: {Summary}", source, summary);
        if (failed > 0 && report is not null)
            log.LogWarning(
                "Run z {Failed} porażkami. Pełne błędy: {Report}. Podgląd w DB: SELECT \"ExternalId\", \"FailureReason\", " +
                "\"AttemptCount\" FROM documents WHERE \"Status\" = {FailedStatus} AND \"Source\" = '{Source}';",
                failed, report.FilePath, (int)Domain.DocumentStatus.Failed, source);
        return summary;

        void LogProgress(int seq)
        {
            if (seq % 50 == 0)
                log.LogInformation("Postęp process {Source}: ins={I} upd={U} skip={S} reemb={R} fail={F} (ostatni: {LastId})",
                    source, inserted, updated, skipped, reembedded, failed, lastId);
        }
    }

    /// <summary>Ogranicza strumień do <paramref name="maxItems"/> pozycji (parytet z dawnym
    /// `processed >= max break`: liczy KAŻDĄ wyemitowaną pozycję, także tę fast-skipowaną).</summary>
    private static async IAsyncEnumerable<RawDocument> Bounded(
        IAsyncEnumerable<RawDocument> source, int? maxItems,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var emitted = 0;
        await foreach (var raw in source.WithCancellation(ct))
        {
            if (maxItems is { } max && emitted >= max) yield break;
            emitted++;
            yield return raw;
        }
    }
}
