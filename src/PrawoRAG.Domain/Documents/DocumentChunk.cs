namespace PrawoRAG.Domain.Documents;

/// <summary>
/// Jednostka indeksowana w bazie wektorowej. Retrieval działa na chunkach, ale cytujemy
/// dokument-rodzica (przez <see cref="Locator"/>). Mapowanie do oryginału: <see cref="CharStart"/>/<see cref="CharEnd"/>.
/// </summary>
public sealed record DocumentChunk
{
    public required int ChunkIndex { get; init; }

    /// <summary>Tekst chunka (z ewentualnym nagłówkiem kontekstowym z segmentu).</summary>
    public required string Text { get; init; }

    /// <summary>Etykieta sekcji/segmentu źródłowego (np. „uzasadnienie", „Art. 148").</summary>
    public string? Section { get; init; }

    public int CharStart { get; init; }
    public int CharEnd { get; init; }

    /// <summary>Liczba tokenów wg tokenizera modelu embeddingów (TEI /tokenize).</summary>
    public int TokenCount { get; init; }

    /// <summary>Lokalizator cytatu odziedziczony z segmentu/dokumentu.</summary>
    public CitationLocator? Locator { get; init; }
}
