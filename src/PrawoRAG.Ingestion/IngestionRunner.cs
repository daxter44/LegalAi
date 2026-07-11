using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Storage;
using PrawoRAG.Storage.Entities;

namespace PrawoRAG.Ingestion;

public sealed record IngestSummary(int Inserted, int Updated, int Skipped, int ReEmbedded, int Failed)
{
    public int Total => Inserted + Updated + Skipped + ReEmbedded + Failed;
    public override string ToString() =>
        $"total={Total} inserted={Inserted} updated={Updated} skipped={Skipped} reembedded={ReEmbedded} failed={Failed}";
}

/// <summary>
/// Orkiestruje przebieg ingestii: iteruje konektor i przetwarza każdy dokument w OSOBNYM scope
/// (świeży DbContext per dokument → brak narastania trackingu), aktualizuje checkpoint <c>sync_state</c>.
/// </summary>
public sealed class IngestionRunner(IServiceScopeFactory scopeFactory, ILogger<IngestionRunner> log)
{
    public async Task<IngestSummary> RunAsync(string source, FetchRequest request, CancellationToken ct)
    {
        var runStart = DateTimeOffset.UtcNow;
        int inserted = 0, updated = 0, skipped = 0, reembedded = 0, failed = 0;

        using (var fetchScope = scopeFactory.CreateScope())
        {
            var connector = fetchScope.ServiceProvider.GetServices<ISourceConnector>()
                .FirstOrDefault(c => string.Equals(c.Source, source, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Brak konektora dla źródła '{source}'.");

            log.LogInformation("Ingestia {Source} start (since={Since})", source, request.SinceModificationDate);

            await foreach (var raw in connector.FetchAsync(request, ct))
            {
                using var docScope = scopeFactory.CreateScope();
                var pipeline = docScope.ServiceProvider.GetRequiredService<IIngestionPipeline>();
                var outcome = (await pipeline.ProcessAsync(raw, ct)).Outcome;
                switch (outcome)
                {
                    case IngestOutcome.Inserted: inserted++; break;
                    case IngestOutcome.Updated: updated++; break;
                    case IngestOutcome.Skipped: skipped++; break;
                    case IngestOutcome.ReEmbedded: reembedded++; break;
                    case IngestOutcome.Failed: failed++; break;
                }
                if ((inserted + updated + skipped + reembedded + failed) % 50 == 0)
                    log.LogInformation("Postęp {Source}: ins={I} upd={U} skip={S} fail={F}", source, inserted, updated, skipped, failed);
            }
        }

        await CheckpointAsync(source, runStart, ct);
        var summary = new IngestSummary(inserted, updated, skipped, reembedded, failed);
        log.LogInformation("Ingestia {Source} koniec: {Summary}", source, summary);
        return summary;
    }

    /// <summary>Watermark = czas startu runu (kolejny przebieg dump pobierze zmiany od tego momentu).</summary>
    private async Task CheckpointAsync(string source, DateTimeOffset runStart, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();
        var state = await db.SyncStates.FindAsync([source], ct) ?? db.SyncStates.Add(new SyncStateEntity { Source = source }).Entity;
        state.LastModificationDate = runStart;
        state.LastRunAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
