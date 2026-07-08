using PrawoRAG.Domain.Documents;

namespace PrawoRAG.Domain.Retrieval;

/// <summary>Zapytanie do retrievera: tekst + filtry metadanych + parametry top-K.</summary>
public sealed record RetrievalQuery
{
    public required string Text { get; init; }

    /// <summary>Liczba finalnych kandydatów po fuzji RRF (kontekst dla LLM).</summary>
    public int TopK { get; init; } = 8;

    /// <summary>Liczba kandydatów z każdej ścieżki (dense, BM25) przed fuzją.</summary>
    public int CandidatesPerPath { get; init; } = 50;

    // --- filtry metadanych ---
    public string? CourtType { get; init; }
    public DateOnly? DateFrom { get; init; }
    public DateOnly? DateTo { get; init; }
    public bool OnlyInForce { get; init; }

    /// <summary>
    /// Minimalna liczba tokenów chunka (0 = bez filtra). Zdegenerowane mini-chunki („⚫", „(",
    /// pojedyncze linie formularzy) mają wysokie cosine do KAŻDEGO zapytania i zaśmiecają top-K.
    /// </summary>
    public int MinChunkTokens { get; init; }
}

/// <summary>Pojedynczy trafiony chunk z lokalizatorem cytatu i wynikiem.</summary>
public sealed record RetrievedChunk
{
    public Guid ChunkId { get; init; }
    public Guid DocumentId { get; init; }
    public required string Text { get; init; }
    public string? Section { get; init; }
    public required string Source { get; init; }
    public required string DocType { get; init; }
    public required string Title { get; init; }
    public string? SourceUrl { get; init; }
    public CitationLocator? Locator { get; init; }

    /// <summary>Wynik fuzji RRF (im wyżej, tym lepiej).</summary>
    public double Score { get; init; }

    /// <summary>Podobieństwo cosine (1 − dystans) z toru gęstego, jeśli chunk był w nim obecny.</summary>
    public double? Similarity { get; init; }

    /// <summary>Score rerankera (cross-encoder), jeśli reranking był włączony. Null = bez rerankingu.</summary>
    public double? RerankScore { get; init; }

    /// <summary>AKT-4: data wejścia w życie, gdy chunk to fragment nowelizacji NIEWCHŁONIĘTEJ do tekstu
    /// jednolitego (dołożony przez <see cref="ITemporalAugmenter"/>). Null = zwykłe źródło.</summary>
    public string? AmendmentEffectiveDate { get; init; }
}

/// <summary>Wynik retrievalu + najwyższe podobieństwo (sygnał dla bramki abstynencji).</summary>
public sealed record RetrievalResult(IReadOnlyList<RetrievedChunk> Chunks, double MaxSimilarity);

public interface IRetriever
{
    Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, CancellationToken ct);
}

/// <summary>
/// Aktualność (AKT-2): dla zwróconych aktów sprawdza nowele NIEWCHŁONIĘTE do tekstu jednolitego i zwraca
/// fragmenty tych nowel dotyczące pytanych artykułów — do DOŁOŻENIA do kontekstu (nigdy nie usuwa istniejących).
/// Zwraca pustą listę, gdy nie ma świeżych nowel — wtedy zachowanie jak dziś.
/// </summary>
public interface ITemporalAugmenter
{
    Task<IReadOnlyList<RetrievedChunk>> AugmentAsync(
        RetrievalQuery query, IReadOnlyList<RetrievedChunk> retrieved, CancellationToken ct);
}
