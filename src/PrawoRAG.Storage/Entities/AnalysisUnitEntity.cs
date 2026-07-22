using System.Text.Json;

namespace PrawoRAG.Storage.Entities;

/// <summary>
/// Wynik analizy JEDNEJ jednostki dokumentu (AN-2). ŚWIADOMIE BEZ kolumny z treścią jednostki
/// i bez embeddingów — patrz <see cref="AnalysisEntity"/> (tajemnica zawodowa). Nagłówek („§ 7")
/// to identyfikator strukturalny, nie treść. Klucz naturalny (AnalysisId, UnitIndex) — retry
/// jednostki NADPISUJE wiersz (upsert), nie dubluje.
/// </summary>
public class AnalysisUnitEntity
{
    public Guid Id { get; set; }
    public Guid AnalysisId { get; set; }
    public AnalysisEntity? Analysis { get; set; }

    /// <summary>= DocUnit.Index (1-based, kolejność dokumentu).</summary>
    public int UnitIndex { get; set; }

    public required string Heading { get; set; }

    /// <summary>Werdykt jako string (enum UnitVerdict żyje w Api): Ok | Risk | NoSources | Error | Unknown.</summary>
    public required string Verdict { get; set; }

    /// <summary>Odpowiedź LLM (bez linii werdyktu) — parafrazuje fragmenty dokumentu (świadomy kompromis).</summary>
    public string? Answer { get; set; }

    /// <summary>Źródła [n] tej jednostki (jsonb, pełny ChatSource) — cytaty w Answer odnoszą się do NICH
    /// (numeracja per jednostka). Wzorzec serializacji jak <see cref="MessageEntity.RetrievedSources"/>.</summary>
    public JsonDocument? Sources { get; set; }

    /// <summary>Spłaszczony wynik anty-fabrykacji (jak <see cref="MessageEntity.CitationClean"/>).</summary>
    public bool? CitationClean { get; set; }

    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public AnalysisUnitFeedbackEntity? Feedback { get; set; }
}
