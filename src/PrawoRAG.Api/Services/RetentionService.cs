using Microsoft.EntityFrameworkCore;
using PrawoRAG.Storage;

namespace PrawoRAG.Api.Services;

/// <summary>
/// Retencja logów pytań (C9/FE-4.4): raz na dobę usuwa rozmowy starsze niż 6 miesięcy. Wiadomości i
/// feedback znikają kaskadą (FK ON DELETE CASCADE). Świadoma decyzja RODO — rewizja przy wzroście.
/// </summary>
public sealed class RetentionService(IServiceScopeFactory scopeFactory, ILogger<RetentionService> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(183); // ~6 miesięcy

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();
                var cutoff = DateTimeOffset.UtcNow - MaxAge;
                var removed = await db.Conversations.Where(c => c.UpdatedAt < cutoff).ExecuteDeleteAsync(ct);
                if (removed > 0) log.LogInformation("Retencja: usunięto {Count} rozmów starszych niż 6 mies.", removed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Retencja: błąd czyszczenia (pominięto, spróbuję ponownie).");
            }

            try { await Task.Delay(Interval, ct); } catch (OperationCanceledException) { break; }
        }
    }
}
