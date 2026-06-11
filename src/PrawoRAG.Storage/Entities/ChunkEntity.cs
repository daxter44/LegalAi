using System.Text.Json;
using NpgsqlTypes;
using Pgvector;

namespace PrawoRAG.Storage.Entities;

/// <summary>
/// Chunk indeksowany w bazie wektorowej. Retrieval po chunkach, cytowanie po dokumencie-rodzicu.
/// </summary>
public class ChunkEntity
{
    public Guid Id { get; set; }

    public Guid DocumentId { get; set; }
    public DocumentEntity? Document { get; set; }

    public int ChunkIndex { get; set; }
    public required string Text { get; set; }
    public string? Section { get; set; }
    public int CharStart { get; set; }
    public int CharEnd { get; set; }
    public int TokenCount { get; set; }

    /// <summary>Wektor gęsty (pgvector). Wymiar = <see cref="PrawoRagDbContext.EmbeddingDimensions"/>. Null dopóki nie zembedowany.</summary>
    public Vector? Embedding { get; set; }

    /// <summary>Model+wersja użyte do embeddingu (np. „sdadas/mmlw-retrieval-roberta-base@v1"). Null = do zembedowania.</summary>
    public string? EmbeddedWith { get; set; }

    /// <summary>Lokalizator cytatu (jsonb) — ELI+artykuł albo sąd+sygnatura+data.</summary>
    public JsonDocument? Locator { get; set; }

    /// <summary>Kolumna tsvector (generowana) do wyszukiwania pełnotekstowego BM25.</summary>
    public NpgsqlTsVector? SearchVector { get; set; }
}
