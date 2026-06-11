namespace PrawoRAG.Domain.Retrieval;

/// <summary>
/// Bramka abstynencji — rdzeń wartości produktu. Gdy retrieval nie ma wystarczającego pokrycia,
/// system NIE generuje odpowiedzi, tylko mówi „nie mam wystarczających źródeł" (zamiast halucynować).
/// </summary>
public static class AbstentionPolicy
{
    /// <summary>Domyślny próg podobieństwa cosine (kalibrowany na golden secie — zadanie 5.3).</summary>
    public const double DefaultThreshold = 0.55;

    public static bool ShouldAbstain(RetrievalResult result, double threshold = DefaultThreshold) =>
        result.Chunks.Count == 0 || result.MaxSimilarity < threshold;

    public const string Message =
        "Nie mam wystarczających źródeł, aby odpowiedzieć. Zawęź pytanie lub wskaż konkretny akt/sygnaturę.";
}
