using System.Text;
using System.Text.Json.Serialization;

namespace PrawoRAG.Eval;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GoldenCategory
{
    /// <summary>Pytanie, na które właściwe źródło JEST w korpusie — oczekiwana odpowiedź z cytatem.</summary>
    InCorpus,
    /// <summary>Pytanie spoza domeny korpusu — oczekiwana abstynencja.</summary>
    OutOfCorpus,
    /// <summary>Pułapka (nieistniejący artykuł, fałszywa przesłanka) — oczekiwana abstynencja/sprostowanie, NIE konfabulacja.</summary>
    Trap,
}

/// <summary>
/// Pozycja golden setu: pytanie + OCZEKIWANE ZACHOWANIE (nie odpowiedź słowo w słowo).
/// Etykiety obiektywne (numer artykułu, „poza domeną") układa programista; niuansowe pytania
/// oznacza się <see cref="NeedsLawyer"/> i nie scoruje jakości merytorycznej do czasu przeglądu prawnika.
/// </summary>
public sealed record GoldenItem
{
    public required string Id { get; init; }
    public required string Question { get; init; }
    public GoldenCategory Category { get; init; }

    /// <summary>Czy poprawnym zachowaniem jest abstynencja (true dla OutOfCorpus/Trap).</summary>
    public bool ShouldAbstain { get; init; }

    // Oczekiwany lokalizator (dla InCorpus, o ile obiektywny). Null = nie scorujemy recall dla tej pozycji.
    public string? ExpectedEli { get; init; }
    public string? ExpectedArticle { get; init; }
    public string? ExpectedCaseNumber { get; init; }

    /// <summary>Pytanie niuansowe — poprawność merytoryczną oceni prawnik; recall/jakości nie scorujemy teraz.</summary>
    public bool NeedsLawyer { get; init; }

    public string? Note { get; init; }
}

public sealed record RetrievedLocator(string? Eli, string? Article, string? CaseNumber);

/// <summary>Co system zrobił dla jednej pozycji (wypełnia runner z wyników retrievalu/czatu).</summary>
public sealed record ItemObservation
{
    public required string Id { get; init; }
    public double MaxSimilarity { get; init; }
    public bool WouldAbstain { get; init; }
    public IReadOnlyList<RetrievedLocator> Retrieved { get; init; } = [];

    /// <summary>Czy cytaty czyste (anty-fabrykacja). Null = czat nie uruchomiony.</summary>
    public bool? CitationsClean { get; init; }
    /// <summary>Czy czat faktycznie odmówił. Null = czat nie uruchomiony.</summary>
    public bool? Abstained { get; init; }
}

public sealed record ItemVerdict(
    string Id, GoldenCategory Category, double MaxSimilarity,
    bool? RetrievalHit, bool AbstentionCorrect, bool? NoHallucination);

/// <summary>Zagregowany raport ewaluacji.</summary>
public sealed record EvalReport
{
    public int Total { get; init; }
    public double Threshold { get; init; }
    public double? RecallAtK { get; init; }
    public double AbstentionAccuracy { get; init; }
    public double? AntiHallucination { get; init; }
    public double MeanSimInCorpus { get; init; }
    public double MeanSimOutOfCorpus { get; init; }
    public int ScoredRecall { get; init; }
    public int ScoredTraps { get; init; }

    public string Format()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== RAPORT EWALUACJI (E5) ===");
        sb.AppendLine($"Pozycji: {Total}   próg abstynencji: {Threshold:F2}");
        sb.AppendLine($"Recall@K (retrieval): {(RecallAtK is { } r ? $"{r:P0}" : "—")}   (na {ScoredRecall} poz. z oczekiwanym źródłem)");
        sb.AppendLine($"Trafność abstynencji: {AbstentionAccuracy:P0}   (na wszystkich {Total})");
        sb.AppendLine($"Anty-halucynacja (pułapki): {(AntiHallucination is { } h ? $"{h:P0}" : "— (czat nieuruchomiony)")}   (na {ScoredTraps} pułapkach)");
        sb.AppendLine($"Śr. similarity: w korpusie {MeanSimInCorpus:F3} vs poza {MeanSimOutOfCorpus:F3}  (rozdział {MeanSimInCorpus - MeanSimOutOfCorpus:+0.000;-0.000})");
        return sb.ToString();
    }
}
