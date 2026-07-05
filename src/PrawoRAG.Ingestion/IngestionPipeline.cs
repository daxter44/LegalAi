using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pgvector;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Documents;
using PrawoRAG.Domain.Embeddings;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Storage;
using PrawoRAG.Storage.Entities;

namespace PrawoRAG.Ingestion;

public enum IngestOutcome { Inserted, Updated, Skipped, ReEmbedded, Failed }

/// <summary>
/// Rdzeń ingestii z idempotencją (plan: „Idempotencja i wznawialność ingestu"):
/// • skip po (source,externalId)+content_hash+status=Indexed — bez normalizacji i embeddingu;
/// • zmiana treści → pełne przetworzenie, transakcyjna podmiana chunków (zero osieroconych);
/// • zmiana modelu embeddingów → re-embed tylko niezgodnych chunków (bez re-normalizacji);
/// • błąd → status Failed + powód + licznik prób (nie blokuje reszty przebiegu).
/// </summary>
public sealed class IngestionPipeline(
    PrawoRagDbContext db,
    IEnumerable<IDocumentNormalizer> normalizers,
    IChunker chunker,
    IEmbeddingProvider embedder,
    ILogger<IngestionPipeline> log)
{
    private readonly Dictionary<string, IDocumentNormalizer> _normalizers =
        normalizers.ToDictionary(n => n.DocType, StringComparer.OrdinalIgnoreCase);

    public async Task<IngestOutcome> ProcessAsync(RawDocument raw, CancellationToken ct)
    {
        var hash = Hashing.Sha256Hex(raw.RawContent);
        var existing = await db.Documents
            .Include(d => d.Chunks)
            .FirstOrDefaultAsync(d => d.Source == raw.Source && d.ExternalId == raw.ExternalId, ct);

        // Skip / warunkowy re-embed dla niezmienionej treści.
        if (existing is { Status: DocumentStatus.Indexed } && existing.ContentHash == hash)
        {
            var stale = existing.Chunks.Where(c => c.EmbeddedWith != embedder.ModelId).ToList();
            if (stale.Count == 0) return IngestOutcome.Skipped;
            return await ReEmbedAsync(stale, ct);
        }

        try
        {
            return await ProcessFreshAsync(raw, hash, existing, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Ingestia nie powiodła się dla {Source}/{Id}", raw.Source, raw.ExternalId);
            await MarkFailedAsync(raw, hash, existing, ex.Message, ct);
            return IngestOutcome.Failed;
        }
    }

    private async Task<IngestOutcome> ProcessFreshAsync(RawDocument raw, string hash, DocumentEntity? existing, CancellationToken ct)
    {
        if (!_normalizers.TryGetValue(raw.DocType, out var normalizer))
            throw new InvalidOperationException($"Brak normalizera dla typu '{raw.DocType}'.");

        var norm = normalizer.Normalize(raw);
        var chunks = await chunker.ChunkAsync(norm, ct);
        var vectors = await embedder.EmbedPassagesAsync(chunks.Select(c => c.Text).ToList(), ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var isNew = existing is null;
        var doc = existing ?? new DocumentEntity
        {
            Id = Guid.CreateVersion7(), Source = raw.Source, ExternalId = raw.ExternalId,
            DocType = raw.DocType, Title = norm.Title, ContentHash = hash,
        };

        doc.Title = norm.Title;
        doc.DocType = raw.DocType;
        doc.SourceUrl = raw.SourceUrl;
        doc.ContentHash = hash;
        // Npgsql akceptuje dla timestamptz tylko offset 0 — źródła bez jawnej strefy (np. ELI) dostają
        // doklejoną strefę lokalną maszyny przy parsowaniu; wymuszamy UTC tutaj, w jedynym miejscu zapisu.
        doc.SourceModificationDate = raw.SourceModificationDate?.ToUniversalTime();
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        if (isNew) doc.IngestedAt = doc.UpdatedAt;
        doc.CourtType = norm.TypedMetadata.GetValueOrDefault("courtType") as string;
        doc.InForce = norm.TypedMetadata.GetValueOrDefault("inForce") as bool?; // akty ELI; dla orzeczeń null
        doc.JudgmentDate = norm.Locator?.JudgmentDate;
        doc.Year = norm.Locator?.JudgmentDate?.Year;
        doc.TypedMetadata = JsonSerializer.SerializeToDocument(norm.TypedMetadata);
        doc.QualityIssues = norm.QualityIssues.ToArray();
        doc.FailureReason = null;
        doc.Status = DocumentStatus.Indexed;

        if (isNew) db.Documents.Add(doc);
        else await db.Chunks.Where(c => c.DocumentId == doc.Id).ExecuteDeleteAsync(ct); // podmiana chunków

        for (var i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            db.Chunks.Add(new ChunkEntity
            {
                Id = Guid.CreateVersion7(), DocumentId = doc.Id, ChunkIndex = c.ChunkIndex,
                Text = c.Text, Section = c.Section, CharStart = c.CharStart, CharEnd = c.CharEnd,
                TokenCount = c.TokenCount, Embedding = new Vector(vectors[i]), EmbeddedWith = embedder.ModelId,
                Locator = c.Locator is null ? null : JsonSerializer.SerializeToDocument(c.Locator),
            });
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return isNew ? IngestOutcome.Inserted : IngestOutcome.Updated;
    }

    private async Task<IngestOutcome> ReEmbedAsync(List<ChunkEntity> stale, CancellationToken ct)
    {
        var vectors = await embedder.EmbedPassagesAsync(stale.Select(c => c.Text).ToList(), ct);
        for (var i = 0; i < stale.Count; i++)
        {
            stale[i].Embedding = new Vector(vectors[i]);
            stale[i].EmbeddedWith = embedder.ModelId;
        }
        await db.SaveChangesAsync(ct);
        return IngestOutcome.ReEmbedded;
    }

    private async Task MarkFailedAsync(RawDocument raw, string hash, DocumentEntity? existing, string reason, CancellationToken ct)
    {
        var doc = existing ?? new DocumentEntity
        {
            Id = Guid.CreateVersion7(), Source = raw.Source, ExternalId = raw.ExternalId,
            DocType = raw.DocType, Title = "(błąd ingestii)", ContentHash = hash, IngestedAt = DateTimeOffset.UtcNow,
        };
        doc.Status = DocumentStatus.Failed;
        doc.FailureReason = reason.Length > 1000 ? reason[..1000] : reason;
        doc.AttemptCount += 1;
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        if (existing is null) db.Documents.Add(doc);
        try { await db.SaveChangesAsync(ct); } catch (Exception ex) { log.LogError(ex, "Nie udało się zapisać statusu Failed."); }
    }
}
