using System.Text.Json;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Sources;

namespace PrawoRAG.Ingestion.Storage;

/// <summary>
/// Koperta surowego dokumentu na dysku: pełny <see cref="RawDocument"/> + metadane fetchu
/// (<see cref="FetchedAt"/>, <see cref="ContentHash"/>, <see cref="SchemaVersion"/>).
/// Round-trip <see cref="SourcePayload"/> (JsonElement) jest wierny — normalizer dostaje
/// identyczny JSON jak z sieci (zob. wzorzec w SaosFixtures: <c>JsonDocument</c> + <c>Clone()</c>).
/// </summary>
public sealed record StoredRawDocument
{
    /// <summary>Wersja schematu koperty — pozwala migrować format magazynu w przyszłości.</summary>
    public int SchemaVersion { get; init; } = 1;

    public required string Source { get; init; }
    public required string ExternalId { get; init; }
    public required string DocType { get; init; }
    public required string RawContent { get; init; }

    /// <summary>Format <see cref="RawContent"/>; brak w starszych plikach = HTML (zgodność wsteczna).</summary>
    public string ContentFormat { get; init; } = ContentFormats.Html;

    public string? SourceUrl { get; init; }
    public DateTimeOffset? SourceModificationDate { get; init; }
    public JsonElement? SourcePayload { get; init; }

    /// <summary>Kiedy pobrano (do audytu/diagnostyki, nie do logiki).</summary>
    public DateTimeOffset FetchedAt { get; init; }

    /// <summary>SHA-256 <see cref="RawContent"/> — szybkie wykrycie zmiany bez ponownej deserializacji.</summary>
    public required string ContentHash { get; init; }

    public static StoredRawDocument FromRaw(RawDocument raw, DateTimeOffset fetchedAt, string contentHash) => new()
    {
        Source = raw.Source,
        ExternalId = raw.ExternalId,
        DocType = raw.DocType,
        RawContent = raw.RawContent,
        ContentFormat = raw.ContentFormat,
        SourceUrl = raw.SourceUrl,
        SourceModificationDate = raw.SourceModificationDate,
        SourcePayload = raw.SourcePayload?.Clone(),
        FetchedAt = fetchedAt,
        ContentHash = contentHash,
    };

    public RawDocument ToRaw() => new()
    {
        Source = Source,
        ExternalId = ExternalId,
        DocType = DocType,
        RawContent = RawContent,
        ContentFormat = ContentFormat,
        SourceUrl = SourceUrl,
        SourceModificationDate = SourceModificationDate,
        SourcePayload = SourcePayload,
    };
}
