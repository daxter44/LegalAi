namespace PrawoRAG.Eval;

/// <summary>Zestaw egzaminacyjny (src/PrawoRAG.Eval/egzaminy-wstepne-2025.json) — patrz commit feat(eval).</summary>
public sealed class ExamSet
{
    public string Name { get; set; } = "";
    public string? StanPrawnyNa { get; set; }
    public List<ExamItem> Items { get; set; } = [];
}

public sealed class ExamItem
{
    public int Nr { get; set; }
    public string Pytanie { get; set; } = "";
    public Dictionary<string, string> Odpowiedzi { get; set; } = [];
    public string Prawidlowa { get; set; } = "";
    public string? PodstawaPrawna { get; set; }
    public string? Zrodlo { get; set; }
    public int NrOryginalny { get; set; }
}

/// <summary>Wynik jednej pozycji w jednym trybie (solo/rag/oracle) — linia raportu JSONL.</summary>
public sealed record ExamItemResult(
    int Nr,
    string Mode,
    char? ModelLetter,
    bool Correct,
    bool? WouldAbstain = null,      // tylko rag: bramka progu retrievalu (LLM i tak pytany — mierzymy oba)
    bool? BasisHit = null,          // tylko rag: czy chunk z podstawy prawnej trafił do kontekstu
    string? BasisHitLeg = null,     // tylko rag: który leg go wniósł (structural/dense/lexical)
    bool? BasisResolved = null,     // rag/oracle: czy akt+artykuł z wykazu rozpoznany w korpusie
    string? RawAnswer = null);
