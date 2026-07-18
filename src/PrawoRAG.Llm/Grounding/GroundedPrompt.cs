using System.Text;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Documents;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Llm.Grounding;

/// <summary>Źródło pokazywane użytkownikowi i numerowane [n] w prompcie/odpowiedzi. AKT-4:
/// <see cref="AmendmentEffectiveDate"/> niepuste ⇔ fragment nowelizacji niewchłoniętej do t.j. (chip w UI).</summary>
public sealed record SourceRef(int Index, string Label, string Title, string? SourceUrl, string Snippet, string? AmendmentEffectiveDate = null);

/// <summary>
/// Buduje ugruntowany prompt: twardy system prompt (odpowiadaj tylko ze źródeł, cytuj [n],
/// abstynencja gdy brak pokrycia) + wiadomość użytkownika z ponumerowanymi źródłami [1..K].
/// </summary>
public static class GroundedPrompt
{
    /// <summary>Fraza, którą LLM ma napisać DOKŁADNIE (reguła 3 w <see cref="SystemPrompt"/>), gdy źródła
    /// nie odpowiadają na pytanie. UI sprawdza nią odpowiedź (Contains, bez rozróżniania wielkości liter),
    /// żeby ukryć panel źródeł — bramka retrievalu (<c>AbstainEvent</c>) tego przypadku nie łapie, bo to
    /// odmowa NA POZIOMIE TREŚCI (LLM ocenił dostarczone źródła jako nietrafne), nie brak pokrycia w progu.</summary>
    public const string RefusalMarker = "Nie mam wystarczających źródeł";

    public const string SystemPrompt =
        """
        Jesteś asystentem prawnym dla polskich prawników. Odpowiadasz WYŁĄCZNIE na podstawie
        dostarczonych źródeł, oznaczonych [1], [2], itd. Zasady bezwzględne:
        1. Zacznij od KONKLUZJI: pierwsze zdanie odpowiada wprost na pytanie w odniesieniu do
           opisanego stanu faktycznego (np. „Nie ponosisz odpowiedzialności, ponieważ…"), dopiero
           potem uzasadnienie. Nie opisuj kolejno źródeł ("Wyrok ten dotyczy...", "Źródło [2]
           mówi o...") i nie wyliczaj abstrakcyjnych „czynników, które biorą pod uwagę sądy" —
           zastosuj prawo do faktów z pytania. Połącz informacje ze wszystkich źródeł w jedną
           spójną odpowiedź w naturalnym języku.
        2. Każdą tezę poprzyj odwołaniem do numeru źródła w nawiasie kwadratowym, np. [1].
           Odpowiedź bez odwołań [n] jest nieprawidłowa.
        2a. Gdy ŹRÓDŁA są podzielone na sekcje PRZEPISY i ORZECZNICTWO: regułę prawną czerp
           z PRZEPISÓW, a orzeczenia traktuj jako przykłady jej zastosowania do konkretnych
           stanów faktycznych — rozstrzygnij, który wzorzec pasuje do faktów z PYTANIA,
           i wywiedź konkluzję z przepisu.
        3. Jeśli dostarczone źródła NIE zawierają odpowiedzi, napisz dokładnie:
           "Nie mam wystarczających źródeł, aby odpowiedzieć." i nic poza tym nie dodawaj.
        4. NIE wymyślaj przepisów, artykułów, sygnatur ani cytatów. Nie korzystaj z wiedzy spoza źródeł.
        5. Cytuj dokładnie; jeśli źródło jest niejednoznaczne — zaznacz to.
        6. Jeśli wśród źródeł jest fragment oznaczony „[NOWELIZACJA …]", to znaczy, że cytowany przepis
           ZMIENIŁA nowela jeszcze niewchłonięta do tekstu jednolitego. Przedstaw wtedy stan PO zmianie:
           wyraźnie napisz, co i od kiedy się zmienia, i zacytuj OBA źródła (tekst jednolity oraz nowelę).
           NIE przepisuj po cichu przepisu jako niezmienionego — zestaw stary tekst i zmianę.
        7. Wcześniejsze wypowiedzi w rozmowie służą WYŁĄCZNIE zrozumieniu kontekstu pytania.
           Każdą tezę odpowiedzi opieraj wyłącznie na ŹRÓDŁACH bieżącej tury; numeracja [n]
           dotyczy tylko bieżących źródeł.
        Odpowiadaj po polsku, rzeczowo i zwięźle.
        """;

    /// <summary>
    /// Zasady doklejane do systemu WYŁĄCZNIE gdy pytanie ma załącznik (DOC-2). Bez dokumentu
    /// system prompt zostaje bajt w bajt dzisiejszy — prompty strojone pod Bielika, instrukcje
    /// o nieistniejącej sekcji to szum i ryzyko regresji (patrz diagnoza 5e).
    /// </summary>
    public const string DocumentRules =
        """
        ZAŁĄCZNIK — zasady dodatkowe (pytanie zawiera sekcję DOKUMENT):
        D1. Fakty stanu faktycznego czerp z sekcji DOKUMENT i oznaczaj cytowaniem [D1], [D2], itd.
        D2. DOKUMENT NIE jest źródłem prawa — podstawę prawną cytuj wyłącznie ze ŹRÓDEŁ jako [n].
            Gdy treść dokumentu jest sprzeczna z przepisem, wskaż tę rozbieżność wprost.
        D3. Dostajesz FRAGMENTY dokumentu, nie całość — jeśli pytanie dotyczy treści nieobecnej
            we fragmentach, napisz wprost, że dołączone fragmenty jej nie zawierają. Nie zgaduj
            zawartości reszty pliku.
        D4. Zasada 3 (fraza odmowy) bez zmian — dotyczy braku PRAWA w ŹRÓDŁACH, nie braków dokumentu.
        """;

    /// <summary>Ile ostatnich zakończonych tur rozmowy wchodzi do promptu (kontekst follow-upów).</summary>
    public const int HistoryTurnsTaken = 4;

    /// <summary>Limit długości JEDNEJ historycznej odpowiedzi w prompcie (koszt/rozmycie kontekstu).</summary>
    public const int MaxHistoryAnswerChars = 1500;

    public static (LlmRequest Request, IReadOnlyList<SourceRef> Sources) Build(string question, IReadOnlyList<RetrievedChunk> chunks)
        => Build(question, chunks, []);

    /// <summary>
    /// Porządek źródeł do ugruntowania: PRZEPISY przed ORZECZNICTWEM (stabilnie w obrębie grup).
    /// Norma prawna jako kotwica na początku listy [1..] — diagnoza 2026-07-17: Bielik dostając
    /// najpierw stos narracji orzeczeń streszczał je, ignorując normę. WOŁAĆ PRZED <see cref="Build"/>
    /// i używać TEGO SAMEGO porządku do kontekstu walidacji anty-fabrykacji — numeracja [n] w prompcie,
    /// panelu źródeł i walidatorze musi być jedna (dlatego porządkuje caller, nie Build).
    /// </summary>
    public static IReadOnlyList<RetrievedChunk> OrderForGrounding(IReadOnlyList<RetrievedChunk> chunks) =>
        chunks.Count == 0 ? chunks : [.. chunks.Where(IsAct), .. chunks.Where(c => !IsAct(c))];

    private static bool IsAct(RetrievedChunk c) => c.DocType == DocTypes.Act;

    /// <summary>
    /// Wariant z historią rozmowy (follow-upy): wcześniejsze tury wchodzą jako naprzemienne wiadomości
    /// User/Assistant PRZED finalną wiadomością z pytaniem i źródłami. Odpowiedzi historyczne są
    /// sanityzowane — markery [n] ZDJĘTE (odnosiły się do źródeł TAMTEJ tury; skopiowane przez model
    /// przeszłyby walidację anty-fabrykacji wskazując inne źródło) i przycięte do limitu.
    /// Tura z abstynencją (Answer=null) → tylko wiadomość User.
    /// </summary>
    public static (LlmRequest Request, IReadOnlyList<SourceRef> Sources) Build(
        string question, IReadOnlyList<RetrievedChunk> chunks, IReadOnlyList<ChatTurn> history)
        => Build(question, chunks, history, []);

    /// <summary>
    /// Wariant z załącznikiem (DOC-2): <paramref name="docFragments"/> (fragmenty dokumentu
    /// użytkownika, już wybrane i uporządkowane) wchodzą jako sekcja DOKUMENT [D1..] między
    /// pytaniem a źródłami, a system dostaje doklejony blok <see cref="DocumentRules"/>.
    /// Pusta lista = zachowanie identyczne jak dotąd (bajt w bajt — zero regresji golden-setu).
    /// </summary>
    public static (LlmRequest Request, IReadOnlyList<SourceRef> Sources) Build(
        string question, IReadOnlyList<RetrievedChunk> chunks, IReadOnlyList<ChatTurn> history,
        IReadOnlyList<string> docFragments)
    {
        var sources = new List<SourceRef>(chunks.Count);
        var sb = new StringBuilder();
        sb.Append("PYTANIE:\n").Append(question);

        if (docFragments.Count > 0)
        {
            sb.Append("\n\nDOKUMENT (fragmenty załącznika użytkownika — fakty, NIE źródło prawa):\n");
            for (var k = 0; k < docFragments.Count; k++)
                sb.Append("[D").Append(k + 1).Append("] ").Append(docFragments[k]).Append("\n\n");
            sb.Append("ŹRÓDŁA:\n");
        }
        else
        {
            sb.Append("\n\nŹRÓDŁA:\n");
        }

        // Podział na sekcje TYLKO gdy są oba typy (norma nie może ginąć wizualnie wśród narracji
        // orzeczeń — diagnoza 2026-07-17/5e); jeden typ = format jak dotąd (zero regresji promptu).
        // Zakłada porządek z OrderForGrounding (przepisy przed orzeczeniami) — przy przeplocie
        // nagłówek pojawia się przy każdej zmianie typu, co nadal jest poprawne, tylko brzydsze.
        var sectioned = chunks.Any(IsAct) && chunks.Any(c => !IsAct(c));
        string? currentSection = null;

        for (var i = 0; i < chunks.Count; i++)
        {
            if (sectioned)
            {
                var section = IsAct(chunks[i]) ? "PRZEPISY:" : "ORZECZNICTWO:";
                if (section != currentSection)
                {
                    sb.Append('\n').Append(section).Append('\n');
                    currentSection = section;
                }
            }

            var n = i + 1;
            var label = LocatorLabel(chunks[i]);
            sources.Add(new SourceRef(n, label, chunks[i].Title, chunks[i].SourceUrl, Snippet(chunks[i].Text), chunks[i].AmendmentEffectiveDate));
            sb.Append('[').Append(n).Append("] ").Append(label).Append('\n')
              .Append(chunks[i].Text).Append("\n\n");
        }

        // Warunkowy system prompt (DOC-2): zasady o dokumencie tylko gdy sekcja DOKUMENT istnieje.
        var system = docFragments.Count > 0 ? SystemPrompt + "\n" + DocumentRules : SystemPrompt;
        var messages = new List<ChatMessage> { new(ChatRole.System, system) };
        foreach (var turn in history.TakeLast(HistoryTurnsTaken))
        {
            if (string.IsNullOrWhiteSpace(turn.Question)) continue;
            AddCoalescing(messages, new ChatMessage(ChatRole.User, turn.Question));
            if (turn.Answer is { } a && !string.IsNullOrWhiteSpace(a))
                messages.Add(new ChatMessage(ChatRole.Assistant, SanitizeHistoryAnswer(a)));
        }
        AddCoalescing(messages, new ChatMessage(ChatRole.User, sb.ToString()));

        var request = new LlmRequest { Messages = messages, Temperature = 0 };
        return (request, sources);
    }

    /// <summary>Scala kolejne wiadomości tej samej roli (tura z abstynencją = samotny User przed następnym
    /// User). Messages API dziś łączy takie tury samo, ale historycznie zwracało 400 „roles must alternate",
    /// a szablony czatu lokalnych modeli (Bielik/llama.cpp) bywają wrażliwe — ścisła naprzemienność jest
    /// bezpieczna dla KAŻDEGO providera.</summary>
    private static void AddCoalescing(List<ChatMessage> messages, ChatMessage next)
    {
        if (messages.Count > 0 && messages[^1] is { } last && last.Role == next.Role)
            messages[^1] = last with { Content = last.Content + "\n\n" + next.Content };
        else
            messages.Add(next);
    }

    private static readonly System.Text.RegularExpressions.Regex CitationMarkerRe =
        new(@"\[\d+\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Historyczna odpowiedź bez markerów [n] (numeracja tamtej tury nie może przeciec do
    /// bieżącej) i przycięta do <see cref="MaxHistoryAnswerChars"/>.</summary>
    public static string SanitizeHistoryAnswer(string answer)
    {
        var clean = CitationMarkerRe.Replace(answer, "").Trim();
        return clean.Length <= MaxHistoryAnswerChars ? clean : clean[..MaxHistoryAnswerChars] + "…";
    }

    /// <summary>Czytelny lokalizator cytatu: akt → tytuł+art.+ELI; orzeczenie → sąd+sygnatura+data.</summary>
    public static string LocatorLabel(RetrievedChunk c)
    {
        var l = c.Locator;
        if (l is null) return c.Title;

        if (!string.IsNullOrEmpty(l.EliId) || !string.IsNullOrEmpty(l.Article))
        {
            var art = l.Article is { } a
                ? $"art. {a}" + (l.Paragraph is { } pg ? $" § {pg}" : "") + (l.Point is { } pt ? $" pkt {pt}" : "")
                : null;
            var parts = new[] { c.Title, art, l.DisplayAddress, l.EliId }
                .Where(s => !string.IsNullOrWhiteSpace(s));
            return string.Join(", ", parts);
        }

        var jp = new[] { l.Court, l.CaseNumber, l.JudgmentDate?.ToString("yyyy-MM-dd") }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        var label = string.Join(", ", jp);
        return string.IsNullOrWhiteSpace(label) ? c.Title : label;
    }

    private static string Snippet(string text, int max = 300) =>
        text.Length <= max ? text : text[..max] + "…";
}
