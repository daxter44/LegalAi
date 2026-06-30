using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Storage;
using PrawoRAG.Storage.Entities;

namespace PrawoRAG.Ingestion;

public sealed record FetchSummary(int Fetched, int SkippedExisting, int Failed)
{
    public int Total => Fetched + SkippedExisting + Failed;
    public override string ToString() =>
        $"total={Total} fetched={Fetched} skipped_existing={SkippedExisting} failed={Failed}";
}

/// <summary>
/// Faza A — pobiera surowe dokumenty ze źródła (konektora) do lokalnego magazynu, IDEMPOTENTNIE:
/// dokument już obecny w magazynie jest pomijany (po (source, externalId)). Checkpoint postępu = sam
/// plik na dysku (crash w połowie nie traci tego, co już zapisane; ponowny fetch dochodzi od miejsca).
/// Watermark <c>sync_state</c> zapisywany na końcu udanego runu — best-effort: niedostępność bazy NIE
/// blokuje pobierania surowych na dysk (fetch ma działać nawet bez schematu bazy).
/// </summary>
public sealed class RawFetchRunner(
    IServiceScopeFactory scopeFactory,
    IRawDocumentStore store,
    ILogger<RawFetchRunner> log)
{
    public async Task<FetchSummary> RunAsync(string source, FetchRequest request, CancellationToken ct)
    {
        var runStart = DateTimeOffset.UtcNow;
        int fetched = 0, skipped = 0, failed = 0;

        using (var fetchScope = scopeFactory.CreateScope())
        {
            var connector = fetchScope.ServiceProvider.GetServices<ISourceConnector>()
                .FirstOrDefault(c => string.Equals(c.Source, source, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Brak konektora dla źródła '{source}'.");

            log.LogInformation("Fetch {Source} start (since={Since})", source, request.SinceModificationDate);

            await foreach (var raw in connector.FetchAsync(request, ct))
            {
                try
                {
                    if (await store.ExistsAsync(raw.Source, raw.ExternalId, ct)) { skipped++; continue; }
                    await store.SaveAsync(raw, ct);
                    fetched++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    log.LogWarning(ex, "Nie zapisano surowego {Source}/{Id}", raw.Source, raw.ExternalId);
                }
                if ((fetched + skipped + failed) % 50 == 0)
                    log.LogInformation("Postęp fetch {Source}: fetched={F} skip={S} fail={X}", source, fetched, skipped, failed);
            }
        }

        await CheckpointAsync(source, runStart, ct);
        var summary = new FetchSummary(fetched, skipped, failed);
        log.LogInformation("Fetch {Source} koniec: {Summary}", source, summary);
        return summary;
    }

    /// <summary>Watermark = czas startu runu (kolejny przebieg dump pobierze zmiany od tego momentu).
    /// Best-effort: jeśli baza niedostępna, fetch i tak zostawił surowe na dysku.</summary>
    private async Task CheckpointAsync(string source, DateTimeOffset runStart, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<PrawoRagDbContext>();
            if (db is null) { log.LogWarning("Brak PrawoRagDbContext — pomijam checkpoint sync_state."); return; }
            var state = await db.SyncStates.FindAsync([source], ct) ?? db.SyncStates.Add(new SyncStateEntity { Source = source }).Entity;
            state.LastModificationDate = runStart;
            state.LastRunAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Checkpoint sync_state nie powiódł się (surowe są zapisane na dysku).");
        }
    }
}
