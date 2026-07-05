using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Embeddings;

/// <summary>
/// <see cref="IReranker"/> oparty o HuggingFace TEI (endpoint <c>/rerank</c>). Cross-encoder bierze
/// surowe pary query×passage (bez prefiksu „zapytanie:"). TEI batchuje serwerowo — wysyłamy jedną listę.
/// Kontrakt: POST {query, texts[], raw_scores:false} → [{index, score}] (wyższy score = trafniejszy).
/// </summary>
public sealed class TeiReranker(HttpClient http, IOptions<RerankerOptions> options) : IReranker
{
    private readonly RerankerOptions _opt = options.Value;

    public string ModelId => _opt.ModelId;

    public async Task<IReadOnlyList<RerankResult>> RerankAsync(string query, IReadOnlyList<string> passages, CancellationToken ct)
    {
        if (passages.Count == 0) return [];

        var req = new RerankRequest { Query = query, Texts = passages, RawScores = false, Truncate = true };
        using var resp = await http.PostAsJsonAsync("/rerank", req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"TEI /rerank {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
        }

        var items = await resp.Content.ReadFromJsonAsync<RerankResponseItem[]>(cancellationToken: ct)
                    ?? throw new InvalidOperationException("TEI /rerank zwróciło pustą odpowiedź.");
        return items.Select(r => new RerankResult(r.Index, r.Score)).ToList();
    }

    private sealed class RerankRequest
    {
        [JsonPropertyName("query")] public required string Query { get; init; }
        [JsonPropertyName("texts")] public required IReadOnlyList<string> Texts { get; init; }
        [JsonPropertyName("raw_scores")] public bool RawScores { get; init; }
        [JsonPropertyName("truncate")] public bool Truncate { get; init; }
    }

    private sealed class RerankResponseItem
    {
        [JsonPropertyName("index")] public int Index { get; init; }
        [JsonPropertyName("score")] public double Score { get; init; }
    }
}
