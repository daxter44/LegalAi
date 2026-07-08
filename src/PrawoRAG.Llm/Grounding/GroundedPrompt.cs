using System.Text;
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
        1. Odpowiedz WPROST na pytanie, zaczynając od sedna — nie opisuj kolejno źródeł
           ("Wyrok ten dotyczy...", "Źródło [2] mówi o..."). Połącz informacje ze wszystkich
           źródeł w jedną spójną odpowiedź w naturalnym języku.
        2. Każdą tezę poprzyj odwołaniem do numeru źródła w nawiasie kwadratowym, np. [1].
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

    /// <summary>Ile ostatnich zakończonych tur rozmowy wchodzi do promptu (kontekst follow-upów).</summary>
    public const int HistoryTurnsTaken = 4;

    /// <summary>Limit długości JEDNEJ historycznej odpowiedzi w prompcie (koszt/rozmycie kontekstu).</summary>
    public const int MaxHistoryAnswerChars = 1500;

    public static (LlmRequest Request, IReadOnlyList<SourceRef> Sources) Build(string question, IReadOnlyList<RetrievedChunk> chunks)
        => Build(question, chunks, []);

    /// <summary>
    /// Wariant z historią rozmowy (follow-upy): wcześniejsze tury wchodzą jako naprzemienne wiadomości
    /// User/Assistant PRZED finalną wiadomością z pytaniem i źródłami. Odpowiedzi historyczne są
    /// sanityzowane — markery [n] ZDJĘTE (odnosiły się do źródeł TAMTEJ tury; skopiowane przez model
    /// przeszłyby walidację anty-fabrykacji wskazując inne źródło) i przycięte do limitu.
    /// Tura z abstynencją (Answer=null) → tylko wiadomość User.
    /// </summary>
    public static (LlmRequest Request, IReadOnlyList<SourceRef> Sources) Build(
        string question, IReadOnlyList<RetrievedChunk> chunks, IReadOnlyList<ChatTurn> history)
    {
        var sources = new List<SourceRef>(chunks.Count);
        var sb = new StringBuilder();
        sb.Append("PYTANIE:\n").Append(question).Append("\n\nŹRÓDŁA:\n");

        for (var i = 0; i < chunks.Count; i++)
        {
            var n = i + 1;
            var label = LocatorLabel(chunks[i]);
            sources.Add(new SourceRef(n, label, chunks[i].Title, chunks[i].SourceUrl, Snippet(chunks[i].Text), chunks[i].AmendmentEffectiveDate));
            sb.Append('[').Append(n).Append("] ").Append(label).Append('\n')
              .Append(chunks[i].Text).Append("\n\n");
        }

        var messages = new List<ChatMessage> { new(ChatRole.System, SystemPrompt) };
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
