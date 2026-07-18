namespace PrawoRAG.Embeddings;

/// <summary>Konfiguracja rerankera (TEI /rerank). Osobna instancja TEI (inny model niż embeddingi).</summary>
public sealed class RerankerOptions
{
    public const string SectionName = "Reranker";

    /// <summary>Czy reranking jest włączony. Domyślnie false — retriever działa bez rerankera.</summary>
    public bool Enabled { get; set; }

    /// <summary>URL instancji TEI z rerankerem (osobna od embeddingów), np. „http://localhost:8081".</summary>
    public string BaseUrl { get; set; } = "http://localhost:8081";

    /// <summary>Model cross-encoder, np. „sdadas/polish-reranker-roberta-v3".</summary>
    public string ModelId { get; set; } = "sdadas/polish-reranker-roberta-v3";

    /// <summary>Twardy limit TEI (CPU), domyślnie 32 — zob. TeiOptions.MaxBatch. Przekroczenie zwraca 422.</summary>
    public int MaxBatch { get; set; } = 32;
}
