using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Storage;

namespace PrawoRAG.Ingestion;

public sealed record ReprocessSummary(int Recovered, int StillFailing, int MissingRaw)
{
    public int Total => Recovered + StillFailing + MissingRaw;
    public override string ToString() =>
        $"total={Total} recovered={Recovered} stillFailing={StillFailing} missingRaw={MissingRaw}";
}

/// <summary>
/// Celowany reprocessing dokumentów w stanie <see cref="DocumentStatus.Failed"/> (np. akty ISAP,
/// które wysypały się na etapie embed — „za długie"). W przeciwieństwie do trybu `process` NIE
/// enumeruje całego magazynu (600 tys. plików), tylko czyta po kluczu naturalnym dokumenty, które
/// baza oznaczyła Failed — i wypisuje ich <c>FailureReason</c> (diagnoza przyczyny) oraz nową próbę.
/// Sam pipeline traktuje Failed jak świeży (nie ma go w fast-skip) → sukces przełącza na Indexed.
/// Reprocessing korzysta z bieżącego kodu (RÓWN-1/2/3), więc awarie przejściowe (timeout na CPU)
/// często znikają na GPU; awarie deterministyczne wracają z tym samym powodem — wtedy trzeba fixu.
/// </summary>
public sealed class ReprocessFailedRunner(
    IServiceScopeFactory scopeFactory,
    IRawDocumentStore store,
    IOptions<ProcessOptions> options,
    ILogger<ReprocessFailedRunner> log)
{
    /// <summary>Wariant produkcyjny: pobiera listę Failed z bazy (id + powód + liczba prób), potem reprocess.</summary>
    public async Task<ReprocessSummary> RunAsync(string source, int? maxItems, CancellationToken ct)
    {
        List<FailedDoc> failed;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();
            var q = db.Documents
                .Where(d => d.Source == source && d.Status == DocumentStatus.Failed)
                .OrderBy(d => d.ExternalId)
                .Select(d => new FailedDoc(d.ExternalId, d.FailureReason, d.AttemptCount));
            if (maxItems is { } max) q = q.Take(max);
            failed = await q.ToListAsync(ct);
        }

        log.LogInformation("Reprocess {Source}: {N} dokumentów Failed do ponowienia", source, failed.Count);
        // Rozkład powodów porażek — od razu widać, czy „za długie" to jeden wzorzec, czy kilka.
        foreach (var g in failed.Where(f => f.Reason is not null)
                     .GroupBy(f => Bucket(f.Reason!)).OrderByDescending(g => g.Count()))
            log.LogInformation("  powód ×{Count}: {Bucket}", g.Count(), g.Key);

        return await RunAsync(source, failed, ct);
    }

    /// <summary>Rdzeń — jawna lista dokumentów Failed (testowalny bez DB). Reprocess równoległy
    /// (<see cref="ProcessOptions.ProcessParallelism"/>), scope DI per dokument.</summary>
    public async Task<ReprocessSummary> RunAsync(string source, IReadOnlyList<FailedDoc> failed, CancellationToken ct)
    {
        long recovered = 0, stillFailing = 0, missingRaw = 0, processed = 0;
        var parallelism = Math.Max(1, options.Value.ProcessParallelism);

        await Parallel.ForEachAsync(failed, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (doc, itemCt) =>
            {
                var raw = await store.ReadAsync(source, doc.ExternalId, itemCt);
                if (raw is null)
                {
                    // Brak surowego w magazynie — nie da się reprocessować bez ponownego fetchu tej pozycji.
                    Interlocked.Increment(ref missingRaw);
                    log.LogWarning("Reprocess: brak surowego dla {Source}/{Id} (fetch tej pozycji ponownie)", source, doc.ExternalId);
                    return;
                }

                using var docScope = scopeFactory.CreateScope();
                var pipeline = docScope.ServiceProvider.GetRequiredService<IIngestionPipeline>();
                var result = await pipeline.ProcessAsync(raw, itemCt);
                if (result.Outcome == IngestOutcome.Failed)
                {
                    Interlocked.Increment(ref stillFailing);
                    log.LogWarning("Reprocess NADAL Failed: {Source}/{Id} [{Stage}] {Error} (próba {Attempt})",
                        source, doc.ExternalId, result.FailureStage,
                        result.Error?.GetBaseException().Message, doc.AttemptCount + 1);
                }
                else
                {
                    Interlocked.Increment(ref recovered);
                }

                if (Interlocked.Increment(ref processed) % 50 == 0)
                    log.LogInformation("Reprocess postęp {Source}: recovered={R} stillFailing={S} missingRaw={M}",
                        source, recovered, stillFailing, missingRaw);
            });

        var summary = new ReprocessSummary((int)recovered, (int)stillFailing, (int)missingRaw);
        log.LogInformation("Reprocess {Source} koniec: {Summary}", source, summary);
        return summary;
    }

    /// <summary>Grupowanie powodów do zwięzłego rozkładu: bierze etap [stage] + pierwsze słowa błędu.</summary>
    private static string Bucket(string reason)
    {
        var trimmed = reason.Length > 80 ? reason[..80] : reason;
        return trimmed.Replace('\n', ' ');
    }
}

/// <summary>Dokument Failed do ponowienia (id + zapisany powód + dotychczasowa liczba prób).</summary>
public sealed record FailedDoc(string ExternalId, string? Reason, int AttemptCount);
