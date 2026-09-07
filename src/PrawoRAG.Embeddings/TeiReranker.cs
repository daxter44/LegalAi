using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Embeddings;

/// <summary>
/// <see cref="IReranker"/> oparty o HuggingFace TEI (endpoint <c>/rerank</c>). Cross-encoder bierze
/// surowe pary query×passage (bez prefiksu „zapytanie:"). Batchowanie PO STRONIE KLIENTA (MaxBatch) —
/// zmierzone: instancja TEI ma twardy `max_client_batch_size` (32), przekroczenie zwraca 422, nie
/// dzieli sama (w przeciwieństwie do zwykłego /embed).
/// Kontrakt: POST {query, texts[], raw_scores:false} → [{index, score}] (wyższy score = trafniejszy;
/// index liczony WEWNĄTRZ danej paczki — przesuwamy o offset przy scalaniu wyników).
/// </summary>
public sealed class TeiReranker(HttpClient http, IOptions<RerankerOptions> options) : IReranker
{
    private readonly RerankerOptions _opt = options.Value;

    public string ModelId => _opt.ModelId;

    public async Task<IReadOnlyList<RerankResult>> RerankAsync(string query, IReadOnlyList<string> passages, CancellationToken ct)
    {
        if (passages.Count == 0) return [];

        var results = new List<RerankResult>(passages.Count);
        for (var offset = 0; offset < passages.Count; offset += _opt.MaxBatch)
        {
            var batch = passages.Skip(offset).Take(_opt.MaxBatch).ToList();
            var req = new RerankRequest { Query = query, Texts = batch, RawScores = false, Truncate = true };
            using var resp = await http.PostAsJsonAsync("/rerank", req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"TEI /rerank {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
            }

            var items = await resp.Content.ReadFromJsonAsync<RerankResponseItem[]>(cancellationToken: ct)
                        ?? throw new InvalidOperationException("TEI /rerank zwróciło pustą odpowiedź.");
            results.AddRange(items.Select(r => new RerankResult(r.Index + offset, r.Score)));
        }
        return results;
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
