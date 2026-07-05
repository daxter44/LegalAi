namespace PrawoRAG.Domain.Documents;

/// <summary>
/// Ustrukturyzowany lokalizator cytatu — to, co pokazujemy prawnikowi i czym weryfikujemy
/// odpowiedź modelu (anty-fabrykacja). Dla aktów: ELI + artykuł/paragraf; dla orzeczeń: sąd + sygnatura + data.
/// </summary>
public sealed record CitationLocator
{
    // --- Akty prawne ---
    /// <summary>Identyfikator ELI, np. „DU/1997/553".</summary>
    public string? EliId { get; init; }
    /// <summary>Numer artykułu, np. „148".</summary>
    public string? Article { get; init; }
    /// <summary>Numer paragrafu, np. „1".</summary>
    public string? Paragraph { get; init; }
    /// <summary>Numer punktu w wyliczeniu paragrafu, np. „1" (art. 52 § 1 pkt 1).</summary>
    public string? Point { get; init; }
    /// <summary>Adres do wyświetlenia człowiekowi, np. „Dz.U. 1997 nr 88 poz. 553".</summary>
    public string? DisplayAddress { get; init; }
    /// <summary>Kotwica w <c>text.html</c>, np. „none_-chpt_XIX-arti_148".</summary>
    public string? Anchor { get; init; }

    // --- Orzeczenia ---
    /// <summary>Sygnatura akt, np. „II K 84/16".</summary>
    public string? CaseNumber { get; init; }
    /// <summary>Nazwa sądu, np. „Sąd Rejonowy w Toruniu".</summary>
    public string? Court { get; init; }
    /// <summary>Data orzeczenia (po walidacji).</summary>
    public DateOnly? JudgmentDate { get; init; }

    /// <summary>URL do oryginału.</summary>
    public string? SourceUrl { get; init; }
}
