using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain.Llm;

namespace PrawoRAG.Llm;

/// <summary>
/// <see cref="ILlmProvider"/> dla serwera zgodnego z OpenAI (Ollama / llama.cpp / LM Studio) — streaming SSE
/// z <c>/chat/completions</c>. Wiadomości (w tym System) idą do tablicy <c>messages</c> z odpowiednimi rolami.
/// Pozwala uruchomić RAG na modelu LOKALNYM (pakiet Diamond) bez zmian w warstwie API.
/// </summary>
public sealed class OpenAiCompatibleLlmProvider(HttpClient http, IOptions<LocalLlmOptions> options) : ILlmProvider
{
    private readonly LocalLlmOptions _opt = options.Value;

    public string ModelId => _opt.Model;

    public async IAsyncEnumerable<string> StreamCompletionAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = request.Messages
            .Select(m => new ApiMessage(
                m.Role switch { ChatRole.System => "system", ChatRole.Assistant => "assistant", _ => "user" },
                m.Content))
            .ToList();

        var body = new ApiRequest
        {
            Model = _opt.Model,
            MaxTokens = request.MaxTokens ?? _opt.MaxTokens,
            Temperature = request.Temperature,
            Messages = messages,
            Stream = true,
        };

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(body),
        };
        if (!string.IsNullOrWhiteSpace(_opt.ApiKey))
            httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.ApiKey);

        using var resp = await http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"LLM lokalny {(int)resp.StatusCode}: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var json = line["data:".Length..].Trim();
            if (json.Length == 0) continue;
            if (json == "[DONE]") yield break;

            string? delta = null;
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array &&
                    choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("delta", out var d) &&
                        d.TryGetProperty("content", out var content) &&
                        content.ValueKind == JsonValueKind.String)
                    {
                        delta = content.GetString();
                    }
                }
            }
            if (!string.IsNullOrEmpty(delta)) yield return delta;
        }
    }

    private sealed class ApiRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; init; }
        [JsonPropertyName("temperature")] public double Temperature { get; init; }
        [JsonPropertyName("messages")] public required IReadOnlyList<ApiMessage> Messages { get; init; }
        [JsonPropertyName("stream")] public bool Stream { get; init; }
    }

    private sealed record ApiMessage([property: JsonPropertyName("role")] string Role, [property: JsonPropertyName("content")] string Content);
}
