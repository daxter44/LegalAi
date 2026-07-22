namespace PrawoRAG.Embeddings;

/// <summary>Konfiguracja klienta TEI (Text Embeddings Inference).</summary>
public sealed class TeiOptions
{
    public const string SectionName = "Embeddings";

    /// <summary>Bazowy URL TEI, np. „http://localhost:8080" (dev) lub „http://tei:80" (compose).</summary>
    public string BaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>Identyfikator modelu — zapisywany w bazie jako <c>embedded_with</c> (lock na życie korpusu).</summary>
    public string ModelId { get; set; } = "sdadas/mmlw-retrieval-roberta-base";

    /// <summary>Wymiar wektora — musi zgadzać się z kolumną v() w bazie (base=768, large-v2=1024).</summary>
    public int Dimensions { get; set; } = 768;

    /// <summary>Prefiks doklejany TYLKO do zapytań (mmlw). Pasaże bez prefiksu.</summary>
    public string QueryPrefix { get; set; } = "zapytanie: ";

    /// <summary>L2-normalizacja wektorów po stronie TEI (wymagana dla porównań cosine / HNSW vector_cosine_ops).</summary>
    public bool Normalize { get; set; } = true;

    /// <summary>
    /// Maksymalny rozmiar batcha (liczba tekstów) w jednym żądaniu do TEI — rządzi ZARÓWNO
    /// /embed, JAK i /tokenize (RÓWN-3: mniej round-tripów przez LAN M4↔Dell przy masowej ingestii).
    /// MUSI być ≤ serwerowego <c>--max-client-batch-size</c> (inaczej 413/422). Domyślnie 32 jest
    /// bezpieczne dla stockowego TEI; deploy GPU (RUNBOOK-3060 ustawia 256) i dev compose podnoszą
    /// serwer do 256, a klient wyrównuje przez <c>Embeddings:MaxBatch</c> w appsettings. Nie wpływa na
    /// serwowanie zapytań (pojedynczy input) — czysty zysk fazy process.
    /// </summary>
    public int MaxBatch { get; set; } = 32;
}
