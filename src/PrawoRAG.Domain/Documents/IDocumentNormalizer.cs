using PrawoRAG.Domain.Sources;

namespace PrawoRAG.Domain.Documents;

/// <summary>
/// Normalizer typu dokumentu: surowy dokument → czysty tekst + segmenty + metadane + lokalizator.
/// Jedna implementacja na typ (orzeczenie, akt, …). Dodanie nowego typu = nowa implementacja + rejestracja.
/// </summary>
public interface IDocumentNormalizer
{
    /// <summary>Typ dokumentu obsługiwany przez normalizer (np. <see cref="DocTypes.Judgment"/>).</summary>
    string DocType { get; }

    /// <summary>Normalizuje dokument. Nie rzuca na błędach jakości danych — zgłasza je w <see cref="NormalizedDocument.QualityIssues"/>.</summary>
    NormalizedDocument Normalize(RawDocument raw);
}
