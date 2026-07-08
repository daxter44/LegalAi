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
    /// Domyślny margines sygnału na korzyść wariantu kontekstowego. Zmierzone na M4: surowe dopytanie
    /// („a co z § 2?") potrafi mieć cosine 0.879 do PRZYPADKOWYCH fragmentów, a wariant kontekstowy
    /// 0.879 do WŁAŚCIWEGO artykułu — różnica rzędu 1e-6 to szum statystyczny, nie sygnał.
    /// Konfigurowalne przez Retrieval:FollowUpSignalMargin (kalibracja bez redeployu).
    /// </summary>
    public const double DefaultSignalMargin = 0.02;

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

    /// <summary>
    /// Wybór wariantu retrievalu przy follow-upie — ASYMETRYCZNY na korzyść kontekstowego: surowe
    /// dopytanie musi pobić wariant kontekstowy o co najmniej <paramref name="margin"/>, żeby wygrać.
    /// Uzasadnienie: koszty pomyłek nie są równe. Fałszywe SUROWE (dopytanie bez treści wygrywa szumem)
    /// = źródła to przypadkowe fragmenty → odpowiedź na śmieciach albo fałszywa odmowa. Fałszywe
    /// KONTEKSTOWE (zmiana tematu uznana za follow-up) = sklejony tekst i tak zawiera całe nowe pytanie
    /// (BM25/dense trafiają nowy temat), a do promptu idzie oryginalne pytanie — degradacja łagodna.
    /// Sam mechanizm istnieje, BO dopytanie nie niesie treści — porównanie łeb w łeb temu przeczyło.
    /// </summary>
    public static bool PickContextual(double rawSignal, double contextualSignal, double margin = DefaultSignalMargin)
        => rawSignal <= contextualSignal + margin;
}
