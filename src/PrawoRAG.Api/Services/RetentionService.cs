using Microsoft.EntityFrameworkCore;
using PrawoRAG.Storage;

namespace PrawoRAG.Api.Services;

/// <summary>
/// Retencja logów pytań (C9/FE-4.4): raz na dobę usuwa rozmowy ORAZ raporty analiz starsze niż
/// 6 miesięcy. Wiadomości/feedback i jednostki analiz znikają kaskadą (FK ON DELETE CASCADE).
/// Świadoma decyzja RODO — rewizja przy wzroście. Na starcie (AN-7) jednorazowy sweep rekordów
/// analiz „Analyzing" → „Interrupted": po restarcie procesu sesje in-memory zginęły, więc żaden
/// taki rekord nie mówi prawdy.
/// </summary>
public sealed class RetentionService(
    IServiceScopeFactory scopeFactory, IAnalysisStore analyses, ILogger<RetentionService> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(183); // ~6 miesięcy

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            var interrupted = await analyses.MarkAllInterruptedAsync(ct);
            if (interrupted > 0)
                log.LogInformation("Start: oznaczono {Count} analiz przerwanych restartem.", interrupted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Start: nie udało się oznaczyć przerwanych analiz (pominięto).");
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();
                var cutoff = DateTimeOffset.UtcNow - MaxAge;
                var removed = await db.Conversations.Where(c => c.UpdatedAt < cutoff).ExecuteDeleteAsync(ct);
                if (removed > 0) log.LogInformation("Retencja: usunięto {Count} rozmów starszych niż 6 mies.", removed);
                var removedAnalyses = await db.Analyses.Where(a => a.UpdatedAt < cutoff).ExecuteDeleteAsync(ct);
                if (removedAnalyses > 0) log.LogInformation("Retencja: usunięto {Count} analiz starszych niż 6 mies.", removedAnalyses);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Retencja: błąd czyszczenia (pominięto, spróbuję ponownie).");
            }

            try { await Task.Delay(Interval, ct); } catch (OperationCanceledException) { break; }
        }
    }
}
