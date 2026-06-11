namespace PrawoRAG.Domain.Documents;

/// <summary>
/// Dzieli znormalizowany dokument na chunki mieszczące się w limicie tokenów modelu embeddingów.
/// Async, bo liczenie tokenów odbywa się przez TEI <c>/tokenize</c> (dokładnie, nie szacunkowo).
/// </summary>
public interface IChunker
{
    Task<IReadOnlyList<DocumentChunk>> ChunkAsync(NormalizedDocument document, CancellationToken ct);
}
