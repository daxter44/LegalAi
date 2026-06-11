namespace PrawoRAG.Llm;

public sealed class ClaudeOptions
{
    public const string SectionName = "Llm:Claude";

    /// <summary>Klucz API (najlepiej z env ANTHROPIC_API_KEY / konfiguracji, nie w repo).</summary>
    public string? ApiKey { get; set; }

    public string Model { get; set; } = "claude-sonnet-4-6";
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
    public string AnthropicVersion { get; set; } = "2023-06-01";
    public int MaxTokens { get; set; } = 1024;
}
