namespace PrawoRAG.Domain.Retrieval;

/// <summary>
/// Heurystyka follow-upów (Krok 1, bez LLM): dopytanie („a co z § 2?") embeduje się bezwartościowo,
/// więc budujemy DRUGI wariant zapytania — poprzednie pytania użytkownika sklejone z bieżącym — i
/// retrieval wybiera wynik z silniejszym sygnałem. Sklejony tekst niesie cytaty z historii
/// („art. 367 KPC"), więc retrieval strukturalny (QU) i augmenter nowel działają na follow-upach
/// bez zmian w nich samych. Czysta — testowalna bez DB/LLM.
/// </summary>
public static class FollowUpQuery
{
    /// <summary>Ile ostatnich POPRZEDNICH pytań użytkownika wchodzi do sklejonego zapytania.
    /// 2 wystarcza na typowe dopytania; więcej rozmywa embedding bieżącej intencji.</summary>
    public const int PreviousQuestionsTaken = 2;

    /// <summary>
    /// Sklejone zapytanie kontekstowe: ostatnie <see cref="PreviousQuestionsTaken"/> poprzednich pytań
    /// (chronologicznie) + bieżące pytanie. Pusta historia → samo pytanie.
    /// </summary>
    public static string Contextualize(IReadOnlyList<string> previousQuestions, string question)
    {
        var prev = previousQuestions
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .TakeLast(PreviousQuestionsTaken)
            .ToList();
        return prev.Count == 0 ? question : string.Join(" ", prev.Append(question));
    }
}
