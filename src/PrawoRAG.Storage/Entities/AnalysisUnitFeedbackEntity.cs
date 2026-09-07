namespace PrawoRAG.Storage.Entities;

/// <summary>
/// Ocena wyniku jednej jednostki analizy (AN-6) — 1:1 z jednostką (jak <see cref="FeedbackEntity"/>
/// z wiadomością). Ten sam słownik werdyktów co czat („up" / „wrong-answer" / „needless-refusal") —
/// spójny materiał do golden setu.
/// </summary>
public class AnalysisUnitFeedbackEntity
{
    public Guid Id { get; set; }
    public Guid AnalysisUnitId { get; set; }
    public AnalysisUnitEntity? Unit { get; set; }

    public required string UserId { get; set; }
    public required string Verdict { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
