using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain.Embeddings;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Storage;

namespace PrawoRAG.Ingestion;

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
        log.LogInformation("Process {Source} start (z magazynu surowych)", source);

        await foreach (var raw in store.EnumerateAsync(source, ct))
        {
            if (maxItems is { } max && processed >= max) break;
            processed++;

            // ODP-1: pominięcie bez scope'a i bez roundtripu do bazy — semantyka jak w pipeline
            // (ten sam Hashing.Sha256Hex; zbiór zawiera tylko Indexed + bieżący model embeddingu).
            if (skipSet.Contains(raw.ExternalId, Hashing.Sha256Hex(raw.RawContent)))
            {
                skipped++;
                LogProgress();
                continue;
            }

            using var docScope = scopeFactory.CreateScope();
            var pipeline = docScope.ServiceProvider.GetRequiredService<IIngestionPipeline>();
            var outcome = await pipeline.ProcessAsync(raw, ct);
            switch (outcome)
            {
                case IngestOutcome.Inserted: inserted++; break;
                case IngestOutcome.Updated: updated++; break;
                case IngestOutcome.Skipped: skipped++; break;
                case IngestOutcome.ReEmbedded: reembedded++; break;
                case IngestOutcome.Failed: failed++; break;
            }
            LogProgress();
        }

        var summary = new IngestSummary(inserted, updated, skipped, reembedded, failed);
        log.LogInformation("Process {Source} koniec: {Summary}", source, summary);
        return summary;

        void LogProgress()
        {
            if (processed % 50 == 0)
                log.LogInformation("Postęp process {Source}: ins={I} upd={U} skip={S} reemb={R} fail={F}",
                    source, inserted, updated, skipped, reembedded, failed);
        }
    }
}
