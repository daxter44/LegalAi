namespace PrawoRAG.Domain.Documents;

/// <summary>
/// Logiczny segment dokumentu wyznaczony przez normalizer (przed sizingiem tokenowym).
/// Dla orzeczeń: sekcja (komparycja/sentencja/uzasadnienie). Dla aktów: artykuł.
/// Chunker zamienia segmenty na chunki mieszczące się w limicie tokenów modelu.
/// </summary>
public sealed record DocumentSegment
{
    /// <summary>Treść segmentu (czysty tekst).</summary>
    public required string Text { get; init; }

    /// <summary>Rodzaj segmentu: „section" (orzeczenie) albo „article" (akt).</summary>
    public string? Kind { get; init; }

    /// <summary>Etykieta, np. „justification" / „Art. 148".</summary>
    public string? Label { get; init; }

    /// <summary>
    /// Nagłówek kontekstowy doklejany do każdego chunka segmentu, np.
    /// „Kodeks karny, Rozdział XIX – Przestępstwa przeciwko życiu i zdrowiu, Art. 148".
    /// Zapewnia, że chunk jest samowystarczalny dla retrieval i cytowania.
    /// </summary>
    public string? ContextHeader { get; init; }

    /// <summary>Lokalizator cytatu dla tego segmentu (dziedziczony przez chunki).</summary>
    public CitationLocator? Locator { get; init; }

    /// <summary>Pozycja początku segmentu w pełnym tekście dokumentu (mapowanie chunk → oryginał).</summary>
    public int CharStart { get; init; }
}
