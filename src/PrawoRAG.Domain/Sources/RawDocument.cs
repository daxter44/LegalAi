using System.Text.Json;

namespace PrawoRAG.Domain.Sources;

/// <summary>
/// Surowy dokument zwrócony przez <see cref="ISourceConnector"/> — przed normalizacją.
/// Niesie zarówno główną treść (<see cref="RawContent"/>: HTML/tekst), jak i ustrukturyzowane
/// pola źródłowe (<see cref="SourcePayload"/>: np. pełny JSON orzeczenia SAOS albo metadane aktu ELI),
/// które konsumuje normalizer właściwy dla danego typu dokumentu.
/// </summary>
public sealed record RawDocument
{
    /// <summary>Klucz źródła, np. <see cref="SourceKeys.Saos"/>.</summary>
    public required string Source { get; init; }

    /// <summary>Identyfikator naturalny w źródle, np. „227221" (SAOS) lub „DU/1997/553" (ELI). Część klucza unikalnego.</summary>
    public required string ExternalId { get; init; }

    /// <summary>Typ dokumentu, np. <see cref="DocTypes.Judgment"/> — wskazuje normalizer.</summary>
    public required string DocType { get; init; }

    /// <summary>Główna treść: HTML orzeczenia (SAOS <c>textContent</c>) lub <c>text.html</c> aktu (ELI).</summary>
    public required string RawContent { get; init; }

    /// <summary>URL oryginału (do cytowania i weryfikacji przez prawnika).</summary>
    public string? SourceUrl { get; init; }

    /// <summary>Data ostatniej modyfikacji w źródle — podstawa synchronizacji przyrostowej i checkpointu.</summary>
    public DateTimeOffset? SourceModificationDate { get; init; }

    /// <summary>Surowe pola źródłowe (metadane). Normalizer czyta z nich sygnaturę, sąd, daty, odwołania itd.</summary>
    public JsonElement? SourcePayload { get; init; }
}
