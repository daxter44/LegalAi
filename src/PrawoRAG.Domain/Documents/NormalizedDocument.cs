namespace PrawoRAG.Domain.Documents;

/// <summary>
/// Dokument po normalizacji: czysty tekst + segmenty + metadane + lokalizator bazowy.
/// Wynik <see cref="IDocumentNormalizer"/>; wejście do <see cref="IChunker"/>.
/// </summary>
public sealed record NormalizedDocument
{
    public required string Source { get; init; }
    public required string ExternalId { get; init; }
    public required string DocType { get; init; }
    public required string Title { get; init; }

    /// <summary>Pełny znormalizowany tekst (do mapowania char offsetów chunków i ewentualnej prezentacji rodzica).</summary>
    public required string PlainText { get; init; }

    /// <summary>Segmenty logiczne (sekcje orzeczenia / artykuły aktu).</summary>
    public required IReadOnlyList<DocumentSegment> Segments { get; init; }

    /// <summary>Bazowy lokalizator dokumentu (sygnatura+sąd albo ELI+adres). Segmenty mogą go uściślać.</summary>
    public CitationLocator? Locator { get; init; }

    public string? SourceUrl { get; init; }
    public DateTimeOffset? SourceModificationDate { get; init; }

    /// <summary>Hash treści źródłowej (SHA-256) — dedup i wykrywanie zmian.</summary>
    public required string ContentHash { get; init; }

    /// <summary>
    /// Metadane specyficzne dla typu (serializowane do jsonb): dla orzeczeń sąd/wydział/sędziowie/
    /// odwołania; dla aktów typ/status/obowiązywanie/słowa kluczowe. Klucz → wartość JSON-owalna.
    /// </summary>
    public IReadOnlyDictionary<string, object?> TypedMetadata { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>Problemy jakości danych źródłowych (np. błędna data) — nie blokują ingestii.</summary>
    public IReadOnlyList<string> QualityIssues { get; init; } = [];
}
