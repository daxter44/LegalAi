using System.Text.Json;
using PrawoRAG.Domain;

namespace PrawoRAG.Storage.Entities;

/// <summary>
/// Dokument źródłowy w bazie — nośnik maszyny stanów ingestii i metadanych.
/// Klucz naturalny (<see cref="Source"/>, <see cref="ExternalId"/>) jest UNIQUE → upsert nigdy nie duplikuje.
/// </summary>
public class DocumentEntity
{
    public Guid Id { get; set; }

    public required string Source { get; set; }
    public required string ExternalId { get; set; }
    public required string DocType { get; set; }
    public required string Title { get; set; }
    public string? SourceUrl { get; set; }

    /// <summary>SHA-256 treści źródłowej — pomijanie niezmienionych i wykrywanie zmian.</summary>
    public required string ContentHash { get; set; }

    public DocumentStatus Status { get; set; } = DocumentStatus.Discovered;

    public DateTimeOffset? SourceModificationDate { get; set; }
    public DateTimeOffset IngestedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // --- pola filtrowalne w retrieval (denormalizacja dla wydajnych WHERE) ---
    public string? CourtType { get; set; }    // COMMON/SUPREME/... (orzeczenia)
    public DateOnly? JudgmentDate { get; set; }

    /// <summary>Znormalizowana sygnatura akt (klucz exact-match: trim + pojedyncze spacje + wielkie
    /// litery — <c>CaseNumberKey.Normalize</c>). Denormalizacja z Locator/TypedMetadata pod DOKŁADNE
    /// wyszukanie orzeczenia po sygnaturze (retrieval strukturalny — sygnatura to identyfikator, nie
    /// zapytanie semantyczne). Null dla aktów.</summary>
    public string? CaseNumber { get; set; }
    public bool? InForce { get; set; }         // akty: czy obowiązuje
    public int? Year { get; set; }

    /// <summary>Metadane specyficzne dla typu (jsonb).</summary>
    public JsonDocument? TypedMetadata { get; set; }

    /// <summary>Problemy jakości danych źródłowych (text[]).</summary>
    public string[] QualityIssues { get; set; } = [];

    // --- kwarantanna błędów ---
    public string? FailureReason { get; set; }
    public int AttemptCount { get; set; }

    public List<ChunkEntity> Chunks { get; set; } = [];
}
