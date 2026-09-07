namespace PrawoRAG.Llm.Grounding;

/// <summary>Co zrobić z wygenerowaną odpowiedzią (Zadanie 10 planu ROU).</summary>
public enum AnswerVerdict
{
    /// <summary>Odpowiedź w porządku — wypuszczamy.</summary>
    Pass,

    /// <summary>Podejrzane odwołania — jedna próba regeneracji na TYM SAMYM kontekście.</summary>
    Regenerate,

    /// <summary>Po regeneracji nadal podejrzane — nie wypuszczamy.</summary>
    Refuse,
}

/// <summary>Decyzja bramki: werdykt + gotowy tekst (instrukcja korygująca albo powód odmowy).</summary>
public sealed record AnswerDecision(AnswerVerdict Verdict, string Text)
{
    public static readonly AnswerDecision Pass = new(AnswerVerdict.Pass, "");
}

/// <summary>
/// Bramka anty-fabrykacji (Zadanie 10 planu ROU) — siostra <c>AbstentionPolicy</c>, ta sama rola:
/// rdzeń wartości produktu.
///
/// PUNKT WYJŚCIA I POWÓD ISTNIENIA: <see cref="CitationValidator"/> już wcześniej wykrywał artykuły
/// i sygnatury nieobecne w dostarczonym kontekście, i miał to pokryte testami — ale
/// <c>IsClean</c> napędzał WYŁĄCZNIE badge ✓/⚠ w UI. Odpowiedź z wymyślonym artykułem wychodziła
/// do użytkownika, tylko z ostrzeżeniem obok. Detektor istniał, nic nie blokował.
///
/// Dlaczego to jest najmocniejszy mechanizm w całym planie: działa na WYJŚCIU, więc nie zależy od
/// tego, czy model zawołał narzędzie, co orzekł router, ani co model sobie „pomyślał". Halucynowane
/// odwołanie nie przechodzi, koniec.
///
/// Czysta funkcja — pętla regeneracji siedzi w <c>ChatService</c>, bo tylko on ma dostęp do LLM
/// i kontekstu.
/// </summary>
public static class AnswerGate
{
    /// <summary>Komunikat odmowy, gdy druga próba nadal cytuje coś, czego nie ma w źródłach.
    /// Wording bez „w źródłach" (2026-08-31) — mówimy o podstawie prawnej, nie o naszym żargonie.</summary>
    public const string RefusalMessage =
        "Nie mogę potwierdzić tej odpowiedzi — model przywołał podstawy prawne, których nie ma " +
        "w znalezionych przepisach i orzeczeniach. Zawęź pytanie lub wskaż konkretny akt/sygnaturę.";

    /// <summary>
    /// <paramref name="alreadyRegenerated"/> = czy budżet naprawczy tury został już zużyty (wspólny
    /// licznik z Zadaniami 12/13 — bez niego mechanizmy naprawcze skumulowałyby się i tura mogłaby
    /// puchnąć do minut).
    /// </summary>
    public static AnswerDecision Decide(CitationCheck check, bool alreadyRegenerated = false)
    {
        // Cytat [n] spoza zakresu to błąd formalny, nie fabrykacja treści — regeneracja go naprawia
        // (model dostaje wprost, że numeruje źródła, których nie ma).
        var outOfRange = check.OutOfRange.Count > 0 || (check.DocOutOfRange?.Count ?? 0) > 0;
        var articles = check.Articles;
        var cases = check.CaseNumbers;

        if (!outOfRange && articles.Count == 0 && cases.Count == 0) return AnswerDecision.Pass;

        if (alreadyRegenerated) return new AnswerDecision(AnswerVerdict.Refuse, RefusalMessage);

        return new AnswerDecision(AnswerVerdict.Regenerate, BuildCorrection(articles, cases, outOfRange));
    }

    /// <summary>
    /// Instrukcja korygująca dla drugiej próby. Wymienia KONKRETNE odwołania, których nie ma
    /// w źródłach — ogólne „nie zmyślaj" model już ma w regule 4 systemowego promptu i widocznie
    /// nie wystarczyło, więc powtarzanie go nic nie doda.
    /// </summary>
    private static string BuildCorrection(
        IReadOnlyList<string> articles, IReadOnlyList<string> cases, bool outOfRange)
    {
        var problems = new List<string>();
        if (articles.Count > 0) problems.Add($"artykuły: {string.Join(", ", articles)}");
        if (cases.Count > 0) problems.Add($"sygnatury: {string.Join(", ", cases)}");

        var sb = new System.Text.StringBuilder();
        sb.Append("KOREKTA: poprzednia wersja odpowiedzi powołała się na odwołania, których NIE MA " +
                  "w dostarczonych ŹRÓDŁACH");
        if (problems.Count > 0) sb.Append(" (").Append(string.Join("; ", problems)).Append(')');
        sb.Append(". Napisz odpowiedź ponownie, opierając się WYŁĄCZNIE na treści źródeł podanych " +
                  "powyżej: nie przywołuj żadnego artykułu ani sygnatury, których nie widzisz w ich " +
                  "tekście. Jeśli źródła nie pozwalają odpowiedzieć na część pytania — napisz to wprost.");
        if (outOfRange)
            sb.Append(" Dodatkowo: odwołuj się tylko do numerów źródeł, które faktycznie istnieją " +
                      "w sekcji ŹRÓDŁA.");
        return sb.ToString();
    }
}
