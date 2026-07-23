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

        // Diagnostyka: zrzut DOKŁADNEGO promptu do pliku, gdy ustawiono PRAWORAG_DUMP_PROMPT. Opt-in,
        // nieaktywne w normalnym biegu — służy do zobaczenia realnego wejścia modelu przy debugowaniu jakości.
        if (Environment.GetEnvironmentVariable("PRAWORAG_DUMP_PROMPT") is { Length: > 0 } dumpPath)
        {
            var dump = string.Join("\n\n", messages.Select(m => $"===== {m.Role.ToUpperInvariant()} =====\n{m.Content}"));
            try { await File.AppendAllTextAsync(dumpPath, $"\n\n########## {DateTime.Now:HH:mm:ss} ##########\n{dump}\n", ct); } catch { /* diagnostyka nie może wywalić żądania */ }
        }

        var body = new ApiRequest
        {
            Model = _opt.Model,
            MaxTokens = request.MaxTokens ?? _opt.MaxTokens,
            Temperature = request.Temperature,
            Messages = messages,
            Stream = true,
            // Finalny chunk z usage (prompt_tokens/completion_tokens) — Ollama i llama.cpp wspierają;
            // serwer, który nie zna pola, po prostu je ignoruje (wtedy fallback = szacunek).
            StreamOptions = new ApiStreamOptions(IncludeUsage: true),
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

        // Diagnostyka: zrzut SUROWYCH linii `data:` ZANIM cokolwiek sparsujemy (PRAWORAG_DUMP_RESPONSE).
        // Bez tego diagnoza formatu thinking to zgadywanie na ślepo (patrz PLAN-OBSLUGA-THINKING-LLM.md).
        var dumpResp = Environment.GetEnvironmentVariable("PRAWORAG_DUMP_RESPONSE") is { Length: > 0 } drp ? drp : null;
        if (dumpResp is not null)
            try { await File.AppendAllTextAsync(dumpResp, $"\n\n########## RESP {DateTime.Now:HH:mm:ss} ##########\n", ct); } catch { }

        // Wydziela „rozumowanie" (Gemini/Gemma) z widocznej treści — emitujemy tylko widoczne delty.
        var splitter = new ReasoningSplitter();
        LlmUsage? usage = null;
        var outputChars = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var json = line["data:".Length..].Trim();
            if (json.Length == 0) continue;
            if (json == "[DONE]") break;
            if (dumpResp is not null)
                try { await File.AppendAllTextAsync(dumpResp, json + "\n", ct); } catch { }

            string? delta = null;
            var isThought = false;
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array &&
                    choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("delta", out var d))
                    {
                        if (d.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                            delta = content.GetString();
                        // Flaga Google: delta.extra_content.google.thought=true ⇒ ta delta to rozumowanie.
                        if (d.TryGetProperty("extra_content", out var ec) && ec.ValueKind == JsonValueKind.Object &&
                            ec.TryGetProperty("google", out var g) && g.ValueKind == JsonValueKind.Object &&
                            g.TryGetProperty("thought", out var th) &&
                            (th.ValueKind == JsonValueKind.True || (th.ValueKind == JsonValueKind.String && th.GetString() == "true")))
                            isThought = true;
                    }
                }

                // Finalny chunk usage (stream_options.include_usage) ma PUSTE choices — nie koliduje z tekstem.
                if (doc.RootElement.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                {
                    usage = new LlmUsage(
                        InputTokens: u.TryGetProperty("prompt_tokens", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null,
                        OutputTokens: u.TryGetProperty("completion_tokens", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null,
                        Estimated: false);
                }
            }

            if (delta is not null || isThought)
            {
                var visible = splitter.Push(delta, isThought);
                if (visible.Length > 0) { outputChars += visible.Length; yield return visible; }
            }
        }
        var tail = splitter.Finish();
        if (tail.Length > 0) { outputChars += tail.Length; yield return tail; }

        // Rozumowanie (jeśli model „myślał") — raz, na końcu, jak OnUsage.
        if (splitter.HasReasoning) request.OnReasoning?.Invoke(splitter.Reasoning);

        // Serwer bez wsparcia stream_options → jawny szacunek ze znaków (~4 znaki/token), nigdy udawany pomiar.
        request.OnUsage?.Invoke(usage ?? new LlmUsage(
            InputTokens: request.Messages.Sum(m => m.Content.Length) / 4,
            OutputTokens: outputChars / 4,
            Estimated: true));
    }

    private sealed class ApiRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; init; }
        [JsonPropertyName("temperature")] public double Temperature { get; init; }
        [JsonPropertyName("messages")] public required IReadOnlyList<ApiMessage> Messages { get; init; }
        [JsonPropertyName("stream")] public bool Stream { get; init; }
        [JsonPropertyName("stream_options")] public ApiStreamOptions? StreamOptions { get; init; }
    }

    private sealed record ApiStreamOptions([property: JsonPropertyName("include_usage")] bool IncludeUsage);

    private sealed record ApiMessage([property: JsonPropertyName("role")] string Role, [property: JsonPropertyName("content")] string Content);
}
