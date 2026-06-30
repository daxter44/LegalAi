namespace PrawoRAG.Llm;

/// <summary>
/// Konfiguracja lokalnego LLM przez serwer zgodny z OpenAI (Ollama / llama.cpp / LM Studio).
/// Pakiet „Diamond" z planu: model lokalnie u klienta, dane nie opuszczają maszyny.
/// </summary>
public sealed class LocalLlmOptions
{
    public const string SectionName = "Llm:Local";

    /// <summary>Bazowy URL API zgodnego z OpenAI. Ollama: <c>http://localhost:11434/v1</c>.</summary>
    public string BaseUrl { get; set; } = "http://localhost:11434/v1";

    /// <summary>Nazwa modelu znana serwerowi (Ollama: tag z `ollama list`). Domyślnie Bielik.</summary>
    public string Model { get; set; } = "speakleash/Bielik-11B-v3.0-DFlash";

    /// <summary>Opcjonalny token (Ollama go ignoruje; llama.cpp/LM Studio bywa wymagany).</summary>
    public string? ApiKey { get; set; }

    public int MaxTokens { get; set; } = 1024;
}
