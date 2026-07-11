using Microsoft.EntityFrameworkCore;
using PrawoRAG.Domain;
using PrawoRAG.Storage;

namespace PrawoRAG.Ingestion;

/// <summary>
/// Zbiór dokumentów do hurtowego pominięcia w fazie „process" (ODP-1). Semantyka wpisu jest
/// IDENTYCZNA ze skipem w <see cref="IngestionPipeline"/>: status Indexed + zgodny content_hash
/// + żaden chunk nie wymaga re-embeddingu (wszystkie <c>EmbeddedWith</c> = bieżący model; dokument
/// z 0 chunków spełnia warunek tak samo, jak dziś <c>stale.Count == 0 → Skipped</c>).
/// Dokumenty Failed, ze zmienioną treścią lub starym modelem NIE wchodzą do zbioru — spadają
/// do pipeline'u dokładnie jak dotąd. ~100 MB RAM przy 551 tys. wpisów, jednorazowo per run.
/// </summary>
public sealed class ProcessSkipSet
{
    public static readonly ProcessSkipSet Empty = new([]);

    private readonly HashSet<string> _keys;

    private ProcessSkipSet(HashSet<string> keys) => _keys = keys;

    public int Count => _keys.Count;

    public bool Contains(string externalId, string contentHash) =>
        _keys.Contains(Key(externalId, contentHash));

    /// <summary>Do testów i budowy poza EF — klucz łączy oba pola separatorem spoza alfabetu id/hex.</summary>
    public static ProcessSkipSet From(IEnumerable<(string ExternalId, string ContentHash)> entries) =>
        new(entries.Select(e => Key(e.ExternalId, e.ContentHash)).ToHashSet(StringComparer.Ordinal));

    /// <summary>Jedno zapytanie (projekcja bez wektorów!) zamiast 551 tys. roundtripów z Include(Chunks).</summary>
    public static async Task<ProcessSkipSet> LoadAsync(
        PrawoRagDbContext db, string source, string embeddingModelId, CancellationToken ct)
    {
        var rows = await db.Documents.AsNoTracking()
            .Where(d => d.Source == source
                     && d.Status == DocumentStatus.Indexed
                     && !d.Chunks.Any(c => c.EmbeddedWith != embeddingModelId))
            .Select(d => new { d.ExternalId, d.ContentHash })
            .ToListAsync(ct);
        return From(rows.Select(r => (r.ExternalId, r.ContentHash)));
    }

    private static string Key(string externalId, string contentHash) => externalId + "\n" + contentHash;
}
