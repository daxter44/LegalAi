namespace PrawoRAG.Domain.Embeddings;

/// <summary>
/// Dostawca embeddingów (abstrakcja jak <see cref="Llm.ILlmProvider"/>). Domyślnie TEI z mmlw,
/// wymienny na cloud bez zmian w workerze/API. KLUCZOWE: model jest zablokowany na życie korpusu —
/// zapytania i pasaże muszą być przetwarzane tym samym modelem (zob. plan, sekcja „Lock modelu embeddingów").
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Identyfikator modelu (np. „sdadas/mmlw-retrieval-roberta-base"). Zapisywany przy chunku jako <c>embedded_with</c>.</summary>
    string ModelId { get; }

    /// <summary>Wymiar wektora (np. 768 dla base, 1024 dla large) — musi zgadzać się z kolumną w bazie.</summary>
    int Dimensions { get; }

    /// <summary>Embeduje pasaże (treść korpusu) — BEZ prefiksu. Batch dla wydajności.</summary>
    Task<IReadOnlyList<float[]>> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken ct);

    /// <summary>Embeduje zapytanie użytkownika — implementacja dokleja wymagany prefiks (mmlw: „zapytanie: ").</summary>
    Task<float[]> EmbedQueryAsync(string query, CancellationToken ct);

    /// <summary>Liczba tokenów wg tokenizera modelu (TEI /tokenize) — używane przez chunker.</summary>
    Task<IReadOnlyList<int>> CountTokensAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
