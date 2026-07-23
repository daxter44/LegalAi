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

    /// <summary>
    /// Most cytowań: maksymalna liczba artykułów dociąganych z cytowań w trafionych orzeczeniach
    /// (0 = wyłączony). Diagnoza 2026-07-17: przepis rządzący (art. 415 KC) jest nieretrievalny dla
    /// pytań opisowych — ale trafione orzeczenia same go cytują; sonda --probe-akty potwierdziła
    /// (415: 3 niezależne dokumenty w top-30; act-only lane obalony — wygrywała pułapka art. 149).
    /// </summary>
    public int CitationBridgeArticles { get; init; } = 2;
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

    /// <summary>Podstawy prawne, na których oparło się orzeczenie (z metadanych dokumentu:
    /// <c>referencedRegulations</c>). Konkretna informacja dla prawnika — pokazywana jako chipy przy
    /// karcie wyroku, bez czytania uzasadnienia. Null/pusto dla aktów i orzeczeń bez tych metadanych.</summary>
    public IReadOnlyList<string>? LegalBases { get; init; }
}

/// <summary>
/// Wynik retrievalu + DWA rozdzielone sygnały (kalibracja przed pilotażem, znalezisko z raportu
/// odmów 2026-07-20): <see cref="MaxSimilarity"/> to ZAWSZE cosine z toru gęstego (stabilna skala,
/// porównywalna między biegami — na niej stoi bramka abstynencji i diagnostyka), a
/// <see cref="RerankTopScore"/> to top-1 cross-encodera (świetny do RANKINGU źródeł, ale odpowiada
/// na inne pytanie: „które z PODANYCH najlepsze", nie „czy wystarcza" — klastruje ~0,99 nawet na
/// śmieciowej puli). Wcześniej reranker po cichu NADPISYWAŁ MaxSimilarity swoim score — próg
/// kalibrowany pod cosine przestawał cokolwiek znaczyć. Null = reranker wyłączony/pusto.
/// </summary>
public sealed record RetrievalResult(
    IReadOnlyList<RetrievedChunk> Chunks, double MaxSimilarity, double? RerankTopScore = null);

public interface IRetriever
{
    Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, CancellationToken ct);
}

/// <summary>
/// Aktualność (AKT-2/4b): zwraca CAŁĄ (zastępczą) listę chunków — oryginalne wejście, z których część może
/// być OZNACZONA (<see cref="RetrievedChunk.AmendmentEffectiveDate"/> ustawione), gdy jej własny dokument
/// jest niewchłoniętą nowelą (trafiła zwykłym retrievalem, nie przez dopasowanie cytatu) — plus DOŁOŻONE
/// nowe fragmenty nowel dotyczące pytanych artykułów. Nigdy nie USUWA istniejących wyników. Gdy nie ma
/// żadnych świeżych nowel do oznaczenia/dołożenia, zwraca <paramref name="retrieved"/> bez zmian.
/// Caller PODMIENIA wynikiem, nie dokleja (kontrakt inny niż „tylko dołożenia").
/// </summary>
public interface ITemporalAugmenter
{
    Task<IReadOnlyList<RetrievedChunk>> AugmentAsync(
        RetrievalQuery query, IReadOnlyList<RetrievedChunk> retrieved, CancellationToken ct);
}
