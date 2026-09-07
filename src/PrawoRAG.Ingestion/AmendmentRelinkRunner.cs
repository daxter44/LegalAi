using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Ingestion.Eli;
using PrawoRAG.Storage;

namespace PrawoRAG.Ingestion;

public sealed record RelinkSummary(int Scanned, int Refreshed, int Unchanged, int Failed)
{
    public override string ToString() =>
        $"scanned={Scanned} refreshed={Refreshed} unchanged={Unchanged} failed={Failed}";
}

/// <summary>
/// AKT-5.2 — relink niewchłoniętych nowel w stanie ustalonym. Dla aktów BAZOWYCH ELI już w bazie
/// (mających <c>consolidatedTextId</c> — tylko one mogą mieć niewchłonięte nowele) pobiera SAME metadane
/// z ELI (JSON, bez text.html/PDF — tani ruch), przelicza listę nowel i — gdy się zmieniła względem
/// zapisanej — patchuje TYLKO klucze <c>unabsorbedAmendments</c>/<c>consolidatedTextId</c> w metadanych.
/// Bez re-embeddingu i bez pobierania treści; nie rusza idempotencji fetchu/process. Odpalane na końcu
/// <c>sync-eli</c>. Pojedynczy błąd sieci nie przerywa całości (akt pomijany, liczony jako Failed).
/// </summary>
public sealed class AmendmentRelinkRunner(
    IServiceScopeFactory scopeFactory,
    ILogger<AmendmentRelinkRunner> log)
{
    public async Task<RelinkSummary> RunAsync(int? maxItems, CancellationToken ct)
    {
        int scanned = 0, refreshed = 0, unchanged = 0, failed = 0;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();
        var connector = scope.ServiceProvider.GetServices<ISourceConnector>().OfType<EliSejmConnector>().FirstOrDefault()
            ?? throw new InvalidOperationException("Brak konektora ELI (EliSejmConnector) — relink niemożliwy.");

        // Akty bazowe ELI: kandydaci = wszystkie akty ELI; filtr „ma t.j." po odczycie metadanych (jsonb).
        var acts = await db.Documents
            .Where(d => d.Source == SourceKeys.Eli && d.DocType == DocTypes.Act)
            .ToListAsync(ct);

        log.LogInformation("Relink ELI start: {Count} aktów-kandydatów.", acts.Count);

        // Data obwieszczenia t.j. (warunek vacatio legis) — cache per bieg: kilka aktów bazowych
        // potrafi wskazywać to samo obwieszczenie, a nieudany fetch nie może wywracać relinku
        // (null = degradacja do starej reguły po samym kluczu ELI).
        var tjDates = new Dictionary<string, DateOnly?>();
        async Task<DateOnly?> TjDateAsync(string tjId)
        {
            if (tjDates.TryGetValue(tjId, out var cached)) return cached;
            DateOnly? date = null;
            try
            {
                using var tjMeta = await connector.FetchActMetadataAsync(tjId, ct);
                date = EliSejmConnector.PublicationDate(tjMeta.RootElement);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Relink: brak daty obwieszczenia {Tj} — degradacja do reguły klucza ELI.", tjId);
            }
            return tjDates[tjId] = date;
        }

        foreach (var doc in acts)
        {
            if (maxItems is { } max && scanned >= max) break;

            var stored = AmendmentRelink.ReadStored(doc.TypedMetadata);
            if (stored.Tj is null) continue; // brak tekstu jednolitego → nie może mieć niewchłoniętych nowel
            scanned++;

            try
            {
                using var metaDoc = await connector.FetchActMetadataAsync(doc.ExternalId, ct);
                var tjId = EliSejmConnector.NewestConsolidatedText(metaDoc.RootElement);
                var tjDate = tjId is null ? null : await TjDateAsync(tjId);
                var fresh = AmendmentRelink.Recompute(metaDoc.RootElement, tjDate);

                if (!AmendmentRelink.NeedsUpdate(stored, fresh)) { unchanged++; continue; }

                doc.TypedMetadata = AmendmentRelink.PatchMetadata(doc.TypedMetadata, fresh.Tj, fresh.Unabsorbed);
                doc.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                refreshed++;
                log.LogInformation("Relink {Ext}: nowele {Before}→{After} (t.j. {Tj}).",
                    doc.ExternalId, stored.Unabsorbed.Count, fresh.Unabsorbed.Count, fresh.Tj);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                log.LogWarning(ex, "Relink pominął akt {Ext} (błąd pobrania metadanych).", doc.ExternalId);
            }
        }

        var summary = new RelinkSummary(scanned, refreshed, unchanged, failed);
        log.LogInformation("Relink ELI koniec: {Summary}", summary);

        // Flaga „wchłonięta nowela" żywi się listami unabsorbedAmendments, które właśnie odświeżyliśmy —
        // przeliczenie MUSI iść w tym samym przebiegu, inaczej retrieval filtrowałby po nieaktualnym
        // stanie (nowela wchłonięta po nowym t.j. konkurowałaby dalej, a świeża — mogłaby zostać wycięta).
        var flags = await RecomputeAbsorbedFlagsAsync(db, ct);
        log.LogInformation("Flagi wchłoniętych nowel: zmienionych {Count} dokumentów.", flags);
        return summary;
    }

    /// <summary>Zbiorcze przeliczenie <c>documents.AbsorbedAmendment</c> (obie strony, idempotentne).
    /// Współdzielone przez relink (stan ustalony) i tryb <c>absorbed-flags</c> (backfill z runbooka).</summary>
    public static Task<int> RecomputeAbsorbedFlagsAsync(PrawoRagDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(AbsorbedAmendments.RecomputeSql, ct);
}
