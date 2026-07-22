using PrawoRAG.Llm.Grounding;

namespace PrawoRAG.Api.Services;

/// <summary>
/// Prompty i parsowanie trybu „Analiza dokumentów" (SPK-3) — czyste funkcje, testowalne bez LLM.
/// Faza map: pytanie do zwykłego czatu RAG = intencja użytkownika + treść JEDNEJ jednostki + wymóg
/// werdyktu w pierwszej linii (zwięzły, ustrukturyzowany wynik — 13 esejów nie zmieści się w oknie
/// modelu przy streszczaniu). Faza reduce: raport składany MECHANICZNIE (werdykty+cytaty przenoszone
/// strukturalnie, nie przez LLM — anty-fabrykacja); LLM pisze tylko streszczenie z zakazem nowych
/// twierdzeń prawnych.
/// </summary>
public static class AnalysisPrompts
{
    public const string VerdictPrefix = "WERDYKT:";

    /// <summary>Pytanie fazy map — idzie przez PEŁNY ChatService (retrieval korpusu + ugruntowanie +
    /// abstynencja za darmo). Treść jednostki w pytaniu zasila też retrieval (BM25/dense po treści §).</summary>
    public static string MapQuestion(string userPrompt, DocUnit unit) =>
        $"""
        {userPrompt}

        Analizowany fragment dokumentu ({unit.Heading}) — oceń WYŁĄCZNIE ten fragment:
        ---
        {unit.Text}
        ---
        Pierwsza linia odpowiedzi to DOKŁADNIE jedno z: „WERDYKT: OK" (fragment nie budzi zastrzeżeń),
        „WERDYKT: RYZYKO" (fragment budzi zastrzeżenia prawne), „WERDYKT: BRAK ŹRÓDEŁ" (źródła nie
        pozwalają ocenić). Potem 1–3 zdania uzasadnienia z cytowaniami [n].
        """;

    /// <summary>Werdykt z pierwszej linii odpowiedzi + odpowiedź bez tej linii (UI pokazuje werdykt
    /// jako badge, nie tekst). Fraza odmowy (reguła 3 promptu) ma pierwszeństwo — to odmowa treściowa,
    /// nawet jeśli model wbrew instrukcji napisał inny werdykt.</summary>
    public static (UnitVerdict Verdict, string Answer) ParseVerdict(string full)
    {
        var text = full.Trim();
        if (text.Contains(GroundedPrompt.RefusalMarker, StringComparison.OrdinalIgnoreCase))
            return (UnitVerdict.NoSources, text);

        var nl = text.IndexOf('\n');
        var firstLine = (nl < 0 ? text : text[..nl]).Trim();
        if (!firstLine.StartsWith(VerdictPrefix, StringComparison.OrdinalIgnoreCase))
            return (UnitVerdict.Unknown, text);

        var rest = nl < 0 ? "" : text[(nl + 1)..].Trim();
        var verdict = firstLine.ToUpperInvariant() switch
        {
            var l when l.Contains("RYZYKO") => UnitVerdict.Risk,
            var l when l.Contains("BRAK") => UnitVerdict.NoSources,
            var l when l.Contains("OK") => UnitVerdict.Ok,
            _ => UnitVerdict.Unknown,
        };
        return (verdict, rest.Length > 0 ? rest : text);
    }

    /// <summary>Etykieta werdyktu dla UI i digestu streszczenia.</summary>
    public static string Label(UnitVerdict v) => v switch
    {
        UnitVerdict.Ok => "OK",
        UnitVerdict.Risk => "RYZYKO",
        UnitVerdict.NoSources => "BRAK ŹRÓDEŁ",
        UnitVerdict.Error => "BŁĄD",
        _ => "?",
    };

    public const string SummarySystemPrompt =
        """
        Jesteś asystentem prawnym. Dostajesz wyniki analizy dokumentu przeprowadzonej fragment po
        fragmencie (werdykt + uzasadnienie per fragment). Napisz zwięzłe streszczenie całości po polsku
        (maksymalnie 120 słów): wskaż najważniejsze ryzyka i fragmenty bez pokrycia w źródłach.
        Zasady bezwzględne: NIE dodawaj żadnych twierdzeń prawnych, przepisów, sygnatur ani ocen,
        których nie ma w dostarczonych wynikach. Nie używaj znaczników [n]. Odwołuj się do fragmentów
        po ich nagłówkach (np. „§ 7").
        """;

    /// <summary>Budżet znaków uzasadnienia jednej jednostki w digestcie streszczenia (okno lokalnego
    /// modelu musi zmieścić wszystkie jednostki).</summary>
    public const int SummaryDigestCharsPerUnit = 220;

    /// <summary>Wejście streszczenia: kompaktowa tabela nagłówek → werdykt → początek uzasadnienia
    /// (bez markerów [n] — numeracja per jednostka nie ma sensu między jednostkami).</summary>
    public static string SummaryInput(string userPrompt, IEnumerable<UnitAnalysis> results)
    {
        var lines = results.Select(r =>
            $"{r.Heading}: {Label(r.Verdict)} — {Digest(r.Answer ?? r.Error ?? "")}");
        return $"Pytanie użytkownika: {userPrompt}\n\nWyniki analizy fragmentów:\n{string.Join("\n", lines)}";
    }

    private static string Digest(string answer)
    {
        var clean = System.Text.RegularExpressions.Regex
            .Replace(answer, @"\[D?\d+\]", "").Replace('\n', ' ').Trim();
        return clean.Length <= SummaryDigestCharsPerUnit ? clean : clean[..SummaryDigestCharsPerUnit] + "…";
    }
}
