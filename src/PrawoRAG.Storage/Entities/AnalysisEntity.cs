namespace PrawoRAG.Storage.Entities;

/// <summary>
/// Nagłówek analizy dokumentu (AN-2). ŚWIADOMIE BEZ TREŚCI DOKUMENTU: zapisujemy wyłącznie raport
/// (prompt użytkownika, nagłówki jednostek, werdykty, odpowiedzi LLM, źródła, streszczenie) — treść
/// załącznika klienta i jej embeddingi NIGDY nie trafiają do bazy (tajemnica zawodowa, decyzja #1
/// planu DOC). NIE „naprawiać" tego dodając kolumnę z tekstem jednostki. Świadomy kompromis:
/// odpowiedzi LLM parafrazują fragmenty dokumentu — akceptowane (jak historia czatu).
/// Status jako string (enum AnalysisStatus żyje w warstwie Api): Analyzing | Done | Failed | Interrupted.
/// Rekord powstaje NA STARCIE analizy (status Analyzing) — po restarcie procesu sesja in-memory
/// znika, więc wiszące Analyzing są oznaczane jako Interrupted (sweep + samonaprawa przy odczycie).
/// </summary>
public class AnalysisEntity
{
    /// <summary>= Id sesji in-memory (jeden identyfikator dla żywej sesji i rekordu DB).</summary>
    public Guid Id { get; set; }

    public required string UserId { get; set; }
    public required string FileName { get; set; }
    public int PageCount { get; set; }

    /// <summary>Intencja użytkownika (pytanie do analizy) — to nasza treść, nie dokument klienta.</summary>
    public required string Prompt { get; set; }

    public required string Status { get; set; }
    public int UnitsTotal { get; set; }
    public bool UnitsTruncated { get; set; }
    public string? Summary { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<AnalysisUnitEntity> Units { get; set; } = [];
}
