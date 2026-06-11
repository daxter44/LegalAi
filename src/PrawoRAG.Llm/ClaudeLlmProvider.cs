using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain.Llm;

namespace PrawoRAG.Llm;

/// <summary>
/// <see cref="ILlmProvider"/> dla Anthropic Messages API (streaming SSE). Wiadomość System z <see cref="LlmRequest"/>
/// trafia do pola `system`, reszta do `messages`. Wymienny na innego dostawcę bez zmian wyżej.
/// </summary>
public sealed class ClaudeLlmProvider(HttpClient http, IOptions<ClaudeOptions> options) : ILlmProvider
{
    private readonly ClaudeOptions _opt = options.Value;

    public string ModelId => _opt.Model;

    public async IAsyncEnumerable<string> StreamCompletionAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var system = string.Join("\n\n", request.Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Content));
        var messages = request.Messages
            .Where(m => m.Role != ChatRole.System)
            .Select(m => new ApiMessage(m.Role == ChatRole.Assistant ? "assistant" : "user", m.Content))
            .ToList();

        var body = new ApiRequest
        {
            Model = _opt.Model,
            MaxTokens = request.MaxTokens ?? _opt.MaxTokens,
            Temperature = request.Temperature,
            System = string.IsNullOrWhiteSpace(system) ? null : system,
            Messages = messages,
            Stream = true,
        };

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(body),
        };
        httpReq.Headers.Add("x-api-key", _opt.ApiKey ?? throw new InvalidOperationException("Brak Llm:Claude:ApiKey (lub env ANTHROPIC_API_KEY)."));
        httpReq.Headers.Add("anthropic-version", _opt.AnthropicVersion);

        using var resp = await http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Claude {(int)resp.StatusCode}: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var json = line["data:".Length..].Trim();
            if (json.Length == 0) continue;

            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "content_block_delta" &&
                doc.RootElement.TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("text", out var text))
            {
                var s = text.GetString();
                if (!string.IsNullOrEmpty(s)) yield return s;
            }
            else if (type == "message_stop")
            {
                yield break;
            }
        }
    }

    private sealed class ApiRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; init; }
        [JsonPropertyName("temperature")] public double Temperature { get; init; }
        [JsonPropertyName("system")] public string? System { get; init; }
        [JsonPropertyName("messages")] public required IReadOnlyList<ApiMessage> Messages { get; init; }
        [JsonPropertyName("stream")] public bool Stream { get; init; }
    }

    private sealed record ApiMessage([property: JsonPropertyName("role")] string Role, [property: JsonPropertyName("content")] string Content);
}
