using System.Text.RegularExpressions;
using PrawoRAG.Domain.Llm;

namespace PrawoRAG.Domain.Retrieval;

/// <summary>
/// Heurystyka follow-upów (Krok 1, bez LLM): dopytanie („a co z § 2?") embeduje się bezwartościowo,
/// więc budujemy DRUGI wariant zapytania — poprzednie pytania użytkownika sklejone z bieżącym — i
/// retrieval wybiera wynik z silniejszym sygnałem. Sklejony tekst niesie cytaty z historii
/// („art. 367 KPC"), więc retrieval strukturalny (QU) i augmenter nowel działają na follow-upach
/// bez zmian w nich samych. Czysta — testowalna bez DB/LLM.
///
/// Anafora typu „a kim jest osoba uprawniona z POWYŻSZEJ ODPOWIEDZI?" odwołuje się do treści i źródeł
/// POPRZEDNIEJ ODPOWIEDZI, nie do wcześniejszych pytań — dlatego wariant kontekstowy foldu­je też kotwice
/// ostatniej realnej odpowiedzi (metadane źródeł + cytaty + krótki fragment). Odtwarza to samodzielne
/// pytanie, które w nowym czacie retrieval trafia poprawnie, bez dodatkowego wywołania LLM.
/// </summary>
public static class FollowUpQuery
{
    /// <summary>Ile ostatnich POPRZEDNICH pytań użytkownika wchodzi do sklejonego zapytania.
    /// 2 wystarcza na typowe dopytania; więcej rozmywa embedding bieżącej intencji.</summary>
    public const int PreviousQuestionsTaken = 2;

    /// <summary>Budżet znaków foldowanego fragmentu odpowiedzi w zapytaniu retrievalowym. ŚWIADOMIE
    /// mniejszy niż <c>GroundedPrompt.MaxHistoryAnswerChars</c> (1500): budżet promptu ma poinformować
    /// LLM, a budżet retrievalu chroni OSTROŚĆ embeddingu bieżącej intencji (mmlw, okno 512 tok).
    /// Fragment niesie rzeczowniki (np. „opuszczenie lasu"), których nie da się odtworzyć z samych
    /// cytatów — to one docierają dense/BM25 do aktu z definicją (ustawa o lasach), gdy ten nie był
    /// źródłem poprzedniej tury.</summary>
    public const int MaxFoldedAnswerChars = 400;

    /// <summary>Ile cytatów z poprzedniej odpowiedzi doklejamy (jak StructuralAsync: garść, nie zalew
    /// numerów artykułów, żeby nie zaszumić BM25).</summary>
    private const int FoldedCitationsTaken = 2;

    private static readonly Regex CitationMarkerRe = new(@"\[\d+\]", RegexOptions.Compiled);

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
    /// Wariant kontekstowy WZBOGACONY o kotwice ostatniej realnej odpowiedzi: poprzednie pytania +
    /// bieżące pytanie (rdzeń), a następnie z ostatniej tury o NIEPUSTEJ odpowiedzi (tury z odmową mają
    /// <see cref="ChatTurn.Answer"/>=null i są pomijane) — metadane źródeł, cytaty wyłuskane z tekstu
    /// i krótki fragment. Bieżące pytanie prowadzi (dominacja intencji); dodatek jest mały (cap), a
    /// wariant surowy nadal konkuruje przez asymetryczny <see cref="PickContextual"/>. Brak niepustej
    /// odpowiedzi → zwraca sam rdzeń (łagodny fallback, zgodny ze starym zachowaniem).
    /// </summary>
    public static string Contextualize(IReadOnlyList<ChatTurn> history, string question)
    {
        var baseCtx = Contextualize(history.Select(t => t.Question).ToList(), question);

        var lastAnswered = history.LastOrDefault(t => !string.IsNullOrWhiteSpace(t.Answer));
        if (lastAnswered is null) return baseCtx;

        var anchors = lastAnswered.SourceAnchors is { Count: > 0 } a
            ? string.Join(" ", a.Where(s => !string.IsNullOrWhiteSpace(s)))
            : "";
        var cites = string.Join(" ", ExtractCitationTokens(lastAnswered.Answer!));
        var snippet = TrimForRetrieval(lastAnswered.Answer!, MaxFoldedAnswerChars);

        // Kolejność: rdzeń (intencja) → źródła → cytaty → fragment. Cytaty PRZED fragmentem, więc
        // przetrwają nawet gdy fragment urwie się przed miejscem cytatu w odpowiedzi.
        return string.Join(" ", new[] { baseCtx, anchors, cites, snippet }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>Cytaty z odpowiedzi zrekonstruowane w formie rozpoznawanej przez tor strukturalny
    /// (<c>art. N [§ p] Skrót</c>) — ten sam <see cref="CitationParser"/>, którego używa retriever.</summary>
    private static IEnumerable<string> ExtractCitationTokens(string answer) =>
        CitationParser.Parse(answer)
            .Take(FoldedCitationsTaken)
            .Select(c => string.Concat(
                "art. ", c.Article,
                c.Paragraph is { } p ? $" § {p}" : "",
                c.ActHint is { } h ? $" {h}" : ""));

    /// <summary>Fragment odpowiedzi do retrievalu: markery [n] zdjęte (numeracja tamtej tury nie ma
    /// tu sensu), przycięty z PRZODU do budżetu (polskie odpowiedzi prawne stawiają regułę/cytat na
    /// początku).</summary>
    private static string TrimForRetrieval(string answer, int max)
    {
        var clean = CitationMarkerRe.Replace(answer, "").Trim();
        return clean.Length <= max ? clean : clean[..max] + "…";
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
