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

    /// <summary>Nazwa modelu znana serwerowi (Ollama: tag z `ollama list`). Domyślnie Bielik v3.0 Instruct GGUF
    /// z rejestru Ollamy (DFlash nie ma GGUF — nie działa w Ollamie).</summary>
    public string Model { get; set; } = "SpeakLeash/bielik-11b-v3.0-instruct:Q5_K_M";

    /// <summary>Opcjonalny token (Ollama go ignoruje; llama.cpp/LM Studio bywa wymagany).</summary>
    public string? ApiKey { get; set; }

    public int MaxTokens { get; set; } = 1024;
}
