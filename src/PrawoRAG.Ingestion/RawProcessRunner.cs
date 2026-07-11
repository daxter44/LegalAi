using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrawoRAG.Domain.Sources;

namespace PrawoRAG.Ingestion;

/// <summary>
/// Faza B — przetwarza surowe dokumenty z LOKALNEGO magazynu przez <see cref="IngestionPipeline"/>.
/// Działa OFFLINE (zero hitów do źródła): to ścieżka re-processingu po zmianie normalizera/chunkera/modelu.
/// Każdy dokument w osobnym scope (świeży DbContext per dokument — brak narastania trackingu).
/// </summary>
public sealed class RawProcessRunner(
    IServiceScopeFactory scopeFactory,
    IRawDocumentStore store,
    ILogger<RawProcessRunner> log)
{
    public async Task<IngestSummary> RunAsync(string source, int? maxItems, CancellationToken ct)
    {
        int inserted = 0, updated = 0, skipped = 0, reembedded = 0, failed = 0, processed = 0;
        log.LogInformation("Process {Source} start (z magazynu surowych)", source);

        await foreach (var raw in store.EnumerateAsync(source, ct))
        {
            if (maxItems is { } max && processed >= max) break;
            processed++;

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
            if (processed % 50 == 0)
                log.LogInformation("Postęp process {Source}: ins={I} upd={U} skip={S} reemb={R} fail={F}",
                    source, inserted, updated, skipped, reembedded, failed);
        }

        var summary = new IngestSummary(inserted, updated, skipped, reembedded, failed);
        log.LogInformation("Process {Source} koniec: {Summary}", source, summary);
        return summary;
    }
}
