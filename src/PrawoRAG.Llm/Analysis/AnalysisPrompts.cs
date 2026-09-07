using PrawoRAG.Llm.Grounding;

namespace PrawoRAG.Llm.Analysis;

/// <summary>
/// Prompty i parsowanie trybu „Analiza dokumentów" (SPK-3) — czyste funkcje, testowalne bez LLM.
/// Faza map: pytanie do zwykłego czatu RAG = intencja użytkownika + treść JEDNEJ jednostki + wymóg
/// werdyktu w pierwszej linii (zwięzły, ustrukturyzowany wynik — 13 esejów nie zmieści się w oknie
/// modelu przy streszczaniu). Faza reduce: raport składany MECHANICZNIE (werdykty+cytaty przenoszone
/// strukturalnie, nie przez LLM — anty-fabrykacja); LLM pisze tylko streszczenie z zakazem nowych
/// twierdzeń prawnych.
/// </summary>
/// <summary>Projekcja wyniku jednostki na potrzeby digestu streszczenia (AJ-1a): nagłówek, werdykt
/// i tekst (uzasadnienie albo komunikat błędu). Pełny <c>UnitAnalysis</c> żyje w Api.</summary>
public sealed record UnitDigest(string Heading, UnitVerdict Verdict, string Text);

public static class AnalysisPrompts
{
    public const string VerdictPrefix = "WERDYKT:";

    /// <summary>Pytanie fazy map — idzie przez PEŁNY ChatService (retrieval korpusu + ugruntowanie +
    /// abstynencja za darmo). Treść jednostki w pytaniu zasila też retrieval (BM25/dense po treści §).</summary>
    public static string MapQuestion(string userPrompt, DocUnit unit) => MapQuestion(userPrompt, unit, profile: null);

    /// <summary>Wariant z profilem dokumentu (AJ-4). Bez profilu — prompt IDENTYCZNY z dotychczasowym
    /// (asercja w testach). Z profilem: blok KONTEKST DOKUMENTU (fakty z całości) nad fragmentem,
    /// żeby ogólna klauzula była oceniana w kontekście TEJ umowy, nie abstrakcyjnie. Profil NIE wchodzi
    /// do zapytania retrievalu (zmierzone 2026-09-03: nie pomaga) — zapytanie to sama treść jednostki,
    /// patrz <see cref="RetrievalQuery"/>.</summary>
    public static string MapQuestion(string userPrompt, DocUnit unit, DocumentProfile? profile)
    {
        if (profile is null || profile.IsEmpty) return MapQuestionCore(userPrompt, unit, context: null);
        var context = $"""
            KONTEKST DOKUMENTU (fakty z całości załącznika, nie oceniaj ich — służą zrozumieniu fragmentu):
            {profile.ToPromptBlock()}

            """;
        return MapQuestionCore(userPrompt, unit, context);
    }

    /// <summary>Budżet zapytania retrievalu w znakach: embedder (mmlw, 512 tokenów) ucina dłuższe;
    /// ~1800 znaków polskiego tekstu prawniczego mieści się z zapasem na kotwicę.</summary>
    public const int RetrievalQueryChars = 1800;

    /// <summary>Zapytanie TYLKO do retrievalu (AJ-4b): sama treść jednostki, BEZ intencji użytkownika
    /// i BEZ instrukcji formatu werdyktu. Dotąd zapytaniem był cały prompt fazy map, ucinany przez
    /// embedder do 512 tokenów; pomiar 2026-09-03 na 17 normach: cały prompt 3/17, sama treść 8/17,
    /// treść + typ dokumentu i strony z profilu 7/17 (odrzucone). Treść ucinana na granicy słowa
    /// do <see cref="RetrievalQueryChars"/>.</summary>
    public static string RetrievalQuery(DocUnit unit)
    {
        var text = unit.Text.Trim();
        if (text.Length <= RetrievalQueryChars) return text;
        var cut = text.LastIndexOf(' ', RetrievalQueryChars - 1);
        return text[..(cut > 0 ? cut : RetrievalQueryChars)];
    }

    private static string MapQuestionCore(string userPrompt, DocUnit unit, string? context) =>
        $"""
        {userPrompt}

        {context}Analizowany fragment dokumentu ({unit.Heading}) — oceń WYŁĄCZNIE ten fragment:
        ---
        {unit.Text}
        ---
        Pierwsza linia odpowiedzi to DOKŁADNIE jedno z:
        „WERDYKT: OK" — fragment zgodny z prawem, nie budzi zastrzeżeń;
        „WERDYKT: RYZYKO WYSOKIE" — fragment sprzeczny z przepisem bezwzględnie obowiązującym albo nieważny;
        „WERDYKT: RYZYKO NISKIE" — fragment niekorzystny, wątpliwy lub do negocjacji, ale nie oczywiście sprzeczny z prawem;
        „WERDYKT: BEZ TREŚCI PRAWNEJ" — fragment nie zawiera żadnego postanowienia do oceny (komparycja, dane stron, opis przedmiotu);
        „WERDYKT: POZA ZAKRESEM" — ocena wymaga dokumentu, którego nie ma w źródłach (akt prawa miejscowego, załącznik, regulamin zewnętrzny) — nazwij ten dokument;
        „WERDYKT: BRAK PODSTAWY" — fragment zawiera postanowienie do oceny, ale źródła nie dają podstawy prawnej.
        Przy RYZYKO WYSOKIE lub NISKIE dodaj dwie kolejne linie: „NARUSZA: " + przepis z cytowaniem [n], którego
        fragment nie respektuje, oraz „DO ROZWAŻENIA: " + jedno zdanie, co zmienić w treści fragmentu.
        Potem 1–3 zdania uzasadnienia z cytowaniami [n].
        """;

    /// <summary>Werdykt z pierwszej linii odpowiedzi + odpowiedź bez tej linii (UI pokazuje werdykt
    /// jako badge, nie tekst). Fraza odmowy (reguła 3 promptu) ma pierwszeństwo — to odmowa treściowa,
    /// nawet jeśli model wbrew instrukcji napisał inny werdykt.</summary>
    public static (UnitVerdict Verdict, string Answer) ParseVerdict(string full)
    {
        var p = ParseUnit(full);
        return (p.Verdict, p.Answer);
    }

    /// <summary>Sparsowana odpowiedź fazy map (AJ-5): werdykt, linie NARUSZA / DO ROZWAŻENIA (tylko przy
    /// ryzyku; null gdy model ich nie podał) i uzasadnienie bez linii strukturalnych.</summary>
    public sealed record ParsedUnit(UnitVerdict Verdict, string Answer, string? Violates, string? Suggestion);

    private const string ViolatesPrefix = "NARUSZA:";
    private const string SuggestionPrefix = "DO ROZWAŻENIA:";

    /// <summary>Parser odpowiedzi fazy map. Pierwsza linia decyduje o werdykcie (zestaw D3); legacy
    /// „RYZYKO" bez wagi → <see cref="UnitVerdict.Risk"/>, „BRAK ŹRÓDEŁ" (stary prompt) → NoSources.
    /// Linie NARUSZA/DO ROZWAŻENIA wycinane z uzasadnienia do osobnych pól — UI pokazuje je jako
    /// akcjonowalne wiersze, nie jako prozę. Fraza odmowy ma pierwszeństwo (odmowa treściowa).</summary>
    public static ParsedUnit ParseUnit(string full)
    {
        var text = full.Trim();
        if (text.Contains(GroundedPrompt.RefusalMarker, StringComparison.OrdinalIgnoreCase))
            return new(UnitVerdict.NoSources, text, null, null);

        var lines = text.Split('\n');
        var firstLine = lines[0].Trim();
        if (!firstLine.StartsWith(VerdictPrefix, StringComparison.OrdinalIgnoreCase))
            return new(UnitVerdict.Unknown, text, null, null);

        var verdict = ParseVerdictLine(firstLine);
        string? violates = null, suggestion = null;
        var rest = new List<string>();
        foreach (var raw in lines.Skip(1))
        {
            var line = raw.Trim().TrimStart('*', '-', ' ');
            if (violates is null && line.StartsWith(ViolatesPrefix, StringComparison.OrdinalIgnoreCase))
                violates = Clean(line[ViolatesPrefix.Length..]);
            else if (suggestion is null && line.StartsWith(SuggestionPrefix, StringComparison.OrdinalIgnoreCase))
                suggestion = Clean(line[SuggestionPrefix.Length..]);
            else rest.Add(raw);
        }
        var answer = string.Join('\n', rest).Trim();
        return new(verdict, answer.Length > 0 ? answer : text, violates, suggestion);

        static string? Clean(string s) => s.Trim().TrimStart('*', ' ').Trim() is { Length: > 0 } v ? v : null;
    }

    private static UnitVerdict ParseVerdictLine(string firstLine)
    {
        var l = firstLine.ToUpperInvariant();
        if (l.Contains("RYZYKO WYSOKIE")) return UnitVerdict.RiskHigh;
        if (l.Contains("RYZYKO NISKIE")) return UnitVerdict.RiskLow;
        if (l.Contains("RYZYKO")) return UnitVerdict.Risk;
        if (l.Contains("BEZ TREŚCI") || l.Contains("BEZ TRESCI")) return UnitVerdict.NoLegalContent;
        if (l.Contains("POZA ZAKRESEM")) return UnitVerdict.OutOfScope;
        if (l.Contains("BRAK")) return UnitVerdict.NoSources;
        if (l.Contains("OK")) return UnitVerdict.Ok;
        return UnitVerdict.Unknown;
    }

    /// <summary>Etykieta werdyktu dla UI i digestu streszczenia (brzmienie D3, 2026-09-02).</summary>
    public static string Label(UnitVerdict v) => v switch
    {
        UnitVerdict.Ok => "OK",
        UnitVerdict.Risk => "RYZYKO",
        UnitVerdict.RiskHigh => "RYZYKO WYSOKIE",
        UnitVerdict.RiskLow => "RYZYKO NISKIE",
        UnitVerdict.NoLegalContent => "BEZ TREŚCI PRAWNEJ",
        UnitVerdict.OutOfScope => "POZA ZAKRESEM",
        UnitVerdict.NoSources => "BRAK PODSTAWY",
        UnitVerdict.Error => "BŁĄD",
        _ => "?",
    };

    public const string SummarySystemPrompt =
        """
        Jesteś asystentem prawnym. Dostajesz pytanie użytkownika, mechanicznie policzony NAGŁÓWEK
        (ile i które fragmenty mają jakie werdykty) oraz wyniki analizy dokumentu fragment po fragmencie
        (werdykt + uzasadnienie). Napisz zwięzłe streszczenie po polsku (maksymalnie 150 słów):
        1. Pierwsze zdanie ODPOWIADA WPROST na pytanie użytkownika — WYŁĄCZNIE jako wniosek z nagłówka
           i werdyktów (np. „Tak, warto rozważyć odwołanie: 3 z 14 fragmentów budzi ryzyko wysokie…");
           jeśli werdykty nie dają podstawy do odpowiedzi, napisz to wprost.
        2. Potem najważniejsze ryzyka (najpierw wysokie) z odwołaniem do nagłówków fragmentów (np. „§ 7")
           i krótko: co narusza / co zmienić — tylko to, co jest w wynikach.
        3. Na końcu jednym zdaniem: fragmenty poza zakresem korpusu lub bez podstawy w źródłach, jeśli są.
        Zasady bezwzględne: NIE dodawaj żadnych twierdzeń prawnych, przepisów, sygnatur ani ocen, których
        nie ma w dostarczonych wynikach. Nie używaj znaczników [n]. Nie oceniaj fragmentów, które dostały OK.
        """;

    /// <summary>Budżet znaków uzasadnienia jednej jednostki w digestcie streszczenia (okno lokalnego
    /// modelu musi zmieścić wszystkie jednostki).</summary>
    public const int SummaryDigestCharsPerUnit = 220;

    /// <summary>Wejście streszczenia: kompaktowa tabela nagłówek → werdykt → początek uzasadnienia
    /// (bez markerów [n] — numeracja per jednostka nie ma sensu między jednostkami).</summary>
    public static string SummaryInput(string userPrompt, IEnumerable<UnitDigest> results)
    {
        var list = results.ToList();
        var lines = list.Select(r =>
            $"{r.Heading}: {Label(r.Verdict)} — {Digest(r.Text)}");
        // Nagłówek mechaniczny (AJ-6) jako jedyna podstawa meta-wniosku streszczenia (D2).
        return $"Pytanie użytkownika: {userPrompt}\n\nNagłówek: {AnalysisReport.Headline(list)}\n\nWyniki analizy fragmentów:\n{string.Join("\n", lines)}";
    }

    private static string Digest(string answer)
    {
        var clean = System.Text.RegularExpressions.Regex
            .Replace(answer, @"\[D?\d+\]", "").Replace('\n', ' ').Trim();
        return clean.Length <= SummaryDigestCharsPerUnit ? clean : clean[..SummaryDigestCharsPerUnit] + "…";
    }
}
