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
