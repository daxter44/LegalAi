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
        Odpowiadaj po polsku, rzeczowo i zwięźle.
        """;

    public static (LlmRequest Request, IReadOnlyList<SourceRef> Sources) Build(string question, IReadOnlyList<RetrievedChunk> chunks)
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

        var request = new LlmRequest
        {
            Messages =
            [
                new ChatMessage(ChatRole.System, SystemPrompt),
                new ChatMessage(ChatRole.User, sb.ToString()),
            ],
            Temperature = 0,
        };
        return (request, sources);
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
