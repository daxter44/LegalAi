using System.Text.Json;

namespace PrawoRAG.Domain.Llm;

/// <summary>
/// Pętla narzędzia <c>szukaj_w_przepisach</c> (Zadanie 15 planu ROU) — model sam formułuje zapytania
/// do bazy, zamiast dostawać sklejkę zrobioną przez kod.
///
/// TRZY RZECZY, KTÓRE MUSZĄ TU ZOSTAĆ:
///
/// 1. <c>tool_choice: required</c> na PIERWSZYM żądaniu tury. Model nie decyduje, CZY szukać —
///    decyduje, CZEGO szukać. To przenosi jego swobodę z polityki gruntowania (tam błąd oznacza
///    odpowiedź prawną z pamięci parametrycznej) na sformułowanie zapytania.
/// 2. Wynik narzędzia idzie przez TĘ SAMĄ ścieżkę retrievalu co czat klasyczny, więc bramka
///    abstynencji i walidacja cytatów działają na nim bez żadnej zmiany.
/// 3. Gdy model NIE zawoła narzędzia (albo serwer nie wspiera `tools` — patrz degradacja
///    w <c>OpenAiCompatibleLlmProvider</c>), wołający MUSI zejść na ścieżkę z bezwarunkowym
///    retrievalem. Sygnalizuje to <see cref="ToolLoopResult.NoToolCall"/>.
///
/// UWAGA na koszt (reguła R1 planu): każde wywołanie narzędzia to DODATKOWE pełne wywołanie modelu
/// głównego, a przy ~41 s rozumowania to najdroższa operacja w systemie. Dlatego żądanie formułujące
/// tool call dostaje niski limit tokenów i zero temperatury — rozumowanie jest potrzebne przy PISANIU
/// odpowiedzi, nie przy wpisaniu frazy do wyszukiwarki.
/// </summary>
public static class ToolLoop
{
    public const string ToolName = "szukaj_w_przepisach";
    private const string ArgumentName = "zapytanie";

    /// <summary>Limit tokenów żądania formułującego wywołanie narzędzia (reguła R2 planu).</summary>
    private const int ToolCallMaxTokens = 256;

    public static readonly LlmTool SearchTool = new(
        ToolName,
        "Szuka przepisów prawa i orzeczeń sądów w bazie prawa polskiego. Wywołaj z zapytaniem " +
        "sformułowanym terminologią ustawową. Bez wywołania tego narzędzia NIE MASZ żadnych źródeł " +
        "i nie wolno Ci odpowiadać na pytania prawne.",
        """
        {"type":"object",
         "properties":{"zapytanie":{"type":"string","description":"Czego szukać w przepisach i orzeczeniach"}},
         "required":["zapytanie"]}
        """);

    /// <param name="Queries">Zapytania, które model wybrał (w kolejności wywołań).</param>
    /// <param name="NoToolCall">
    /// Model nie zawołał narzędzia ani razu — wołający musi zejść na ścieżkę z bezwarunkowym
    /// retrievalem. To NIE jest błąd, tylko sygnał degradacji: serwer mógł nie wspierać `tools`.
    /// </param>
    public sealed record ToolLoopResult(IReadOnlyList<string> Queries, bool NoToolCall);

    /// <summary>
    /// Zbiera zapytania, które model chce wykonać. Świadomie NIE wykonuje retrievalu sama —
    /// wołający (ChatService) ma DbContext, kanał zdarzeń i bramki, a ta klasa zostaje czysta
    /// i testowalna bez bazy.
    /// </summary>
    /// <param name="maxToolCalls">Górny limit wywołań w turze — twardy hamulec na koszt.</param>
    public static async Task<ToolLoopResult> CollectQueriesAsync(
        ILlmProvider llm, IReadOnlyList<ChatMessage> messages, int maxToolCalls, CancellationToken ct)
    {
        var calls = new List<LlmToolCall>();

        var request = new LlmRequest
        {
            Messages = messages,
            Temperature = 0,
            MaxTokens = ToolCallMaxTokens,
            Tools = [SearchTool],
            // required — model nie ma prawa odpowiedzieć bez sięgnięcia do bazy.
            ToolChoice = "required",
            OnToolCall = calls.Add,
        };

        // Strumień odrzucamy: w tym żądaniu interesuje nas WYŁĄCZNIE wywołanie narzędzia. Widoczny
        // tekst (gdyby model coś napisał obok) byłby odpowiedzią BEZ źródeł — dokładnie tym, czego
        // nie wolno wypuścić.
        await foreach (var _ in llm.StreamCompletionAsync(request, ct)) { }

        var queries = calls
            .Where(c => c.Name == ToolName)
            .Select(ExtractQuery)
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Select(q => q!)
            .Take(Math.Max(1, maxToolCalls))
            .ToList();

        return new ToolLoopResult(queries, NoToolCall: queries.Count == 0);
    }

    /// <summary>
    /// Wyciąga zapytanie z argumentów wywołania. Model bywa niedokładny (ucięty JSON przy limicie
    /// tokenów, inna nazwa pola), więc zamiast rzucać zwracamy null i wołający degraduje do
    /// bezwarunkowego retrievalu — nigdy do odpowiedzi bez źródeł.
    /// </summary>
    private static string? ExtractQuery(LlmToolCall call)
    {
        if (string.IsNullOrWhiteSpace(call.ArgumentsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            if (doc.RootElement.TryGetProperty(ArgumentName, out var q) && q.ValueKind == JsonValueKind.String)
                return q.GetString();

            // Fallback: jedyna wartość tekstowa w obiekcie — model czasem nazwie pole inaczej
            // („query", „q"), a zapytanie jest oczywiste.
            var strings = doc.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToList();
            return strings.Count == 1 ? strings[0].Value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
