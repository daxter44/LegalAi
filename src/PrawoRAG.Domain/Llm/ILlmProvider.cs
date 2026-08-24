namespace PrawoRAG.Domain.Llm;

public enum ChatRole { System, User, Assistant }

public sealed record ChatMessage(ChatRole Role, string Content);

/// <summary>
/// Zużycie tokenów jednej generacji. <see cref="Estimated"/>=true, gdy serwer nie raportuje usage
/// (np. stary llama.cpp) i liczby są szacunkiem ze znaków — UI oznacza je „~", nigdy nie udajemy pomiaru.
/// </summary>
public sealed record LlmUsage(int? InputTokens, int? OutputTokens, bool Estimated);

/// <summary>
/// Żądanie do LLM. Kontekst ugruntowania (chunki ze źródłami) jest wpleciony w wiadomości
/// przez warstwę API — provider jest cienkim transportem, by łatwo wymieniać Claude/OpenAI/Bielik.
/// </summary>
public sealed record LlmRequest
{
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>Temperatura — domyślnie 0 dla determinizmu i ograniczenia konfabulacji.</summary>
    public double Temperature { get; init; }

    public int? MaxTokens { get; init; }

    /// <summary>
    /// Wywoływany, gdy provider pozna zużycie tokenów (usage przychodzi NA KOŃCU strumienia SSE,
    /// a kontrakt streamuje gołe delty tekstu — callback omija przepisywanie call-site'ów na typ
    /// unijny). Null = wołający nie jest zainteresowany (Eval, testy) — zero kosztu.
    /// </summary>
    public Action<LlmUsage>? OnUsage { get; init; }

    /// <summary>
    /// Wywoływany RAZ na końcu strumienia z „rozumowaniem" (thinking/CoT) modelu, jeśli je wydzielono
    /// (Gemini/Gemma flaga <c>google.thought</c> albo tagi <c>&lt;think&gt;</c>). Strumień
    /// <see cref="ILlmProvider.StreamCompletionAsync"/> emituje WYŁĄCZNIE treść widoczną — rozumowanie
    /// idzie tędy, więc nie zaśmieca odpowiedzi, walidacji cytatów ani parsowania werdyktu analizy.
    /// Null / brak rozumowania → nie wołany (Claude/Bielik: zero zmian).
    /// </summary>
    public Action<string>? OnReasoning { get; init; }

    /// <summary>
    /// Wywoływany dla KAŻDEJ delty rozumowania, w trakcie strumienia (Zadanie 1 planu ROU).
    /// Powód pomiarowy: rozumowanie to ~41 z ~85 s odpowiedzi (PRAWORAG_LOG_TIMING) i fizycznie
    /// leci po drucie token po tokenie — a użytkownik dostawał je dopiero po zakończeniu generacji,
    /// więc przez większość czekania UI nie miało CO pokazać.
    /// Konkatenacja wszystkich wywołań == argument <see cref="OnReasoning"/> na końcu (test
    /// równoważności) — emisja na żywo NIE zmienia tego, co trafia do historii.
    /// Null = wołający nie jest zainteresowany (Eval, testy) — zero kosztu.
    /// </summary>
    public Action<string>? OnReasoningDelta { get; init; }
}

/// <summary>
/// Dostawca LLM (wymienny). MVP: Claude/OpenAI (cloud). Później Bielik lokalnie (pakiet Diamond) —
/// bez zmian w warstwie wyżej dzięki tej abstrakcji.
/// </summary>
public interface ILlmProvider
{
    /// <summary>Identyfikator modelu (do logów/telemetrii kosztów).</summary>
    string ModelId { get; }

    /// <summary>Strumieniuje odpowiedź token po tokenie (SSE w API).</summary>
    IAsyncEnumerable<string> StreamCompletionAsync(LlmRequest request, CancellationToken ct);
}
