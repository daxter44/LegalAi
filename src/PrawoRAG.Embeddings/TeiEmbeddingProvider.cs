using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain.Embeddings;

namespace PrawoRAG.Embeddings;

/// <summary>
/// <see cref="IEmbeddingProvider"/> oparty o HuggingFace TEI (HTTP). Pasaże bez prefiksu,
/// zapytania z prefiksem „zapytanie: " (mmlw). Liczenie tokenów przez /tokenize (dokładnie).
/// </summary>
public sealed class TeiEmbeddingProvider(HttpClient http, IOptions<TeiOptions> options) : IEmbeddingProvider
{
    private readonly TeiOptions _opt = options.Value;

    public string ModelId => _opt.ModelId;
    public int Dimensions => _opt.Dimensions;

    public async Task<IReadOnlyList<float[]>> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken ct)
    {
        if (passages.Count == 0) return [];
        return await EmbedAsync(passages, ct);
    }

    public async Task<float[]> EmbedQueryAsync(string query, CancellationToken ct)
    {
        var result = await EmbedAsync([_opt.QueryPrefix + query], ct);
        return result[0];
    }

    private async Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        var all = new List<float[]>(inputs.Count);
        foreach (var batch in Batch(inputs))
        {
            var req = new EmbedRequest { Inputs = batch, Normalize = _opt.Normalize, Truncate = true };
            using var resp = await http.PostAsJsonAsync("/embed", req, ct);
            await EnsureOkAsync(resp, ct);
            var vectors = await resp.Content.ReadFromJsonAsync<float[][]>(cancellationToken: ct)
                          ?? throw new InvalidOperationException("TEI /embed zwróciło pustą odpowiedź.");
            foreach (var v in vectors)
            {
                if (v.Length != _opt.Dimensions)
                    throw new InvalidOperationException(
                        $"Niezgodność wymiaru embeddingu: model zwrócił {v.Length}, oczekiwano {_opt.Dimensions} (TeiOptions.Dimensions vs kolumna vector() w bazie).");
                all.Add(v);
            }
        }
        return all.ToArray();
    }

    public async Task<IReadOnlyList<int>> CountTokensAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts.Count == 0) return [];
        var counts = new List<int>(texts.Count);
        foreach (var batch in Batch(texts))
        {
            var req = new TokenizeRequest { Inputs = batch, AddSpecialTokens = true };
            using var resp = await http.PostAsJsonAsync("/tokenize", req, ct);
            await EnsureOkAsync(resp, ct);
            // /tokenize zwraca listę-per-input: [[{id,text,...}], ...]
            var perInput = await resp.Content.ReadFromJsonAsync<TokenInfo[][]>(cancellationToken: ct)
                           ?? throw new InvalidOperationException("TEI /tokenize zwróciło pustą odpowiedź.");
            counts.AddRange(perInput.Select(tokens => tokens.Length));
        }
        return counts;
    }

    /// <summary>Dzieli wejście na batche ≤ MaxBatch (TEI CPU ma twardy limit, domyślnie 32).</summary>
    private IEnumerable<IReadOnlyList<string>> Batch(IReadOnlyList<string> items)
    {
        for (var i = 0; i < items.Count; i += _opt.MaxBatch)
            yield return items.Skip(i).Take(_opt.MaxBatch).ToList();
    }

    private static async Task EnsureOkAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"TEI {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
    }

    private sealed class EmbedRequest
    {
        [JsonPropertyName("inputs")] public required IReadOnlyList<string> Inputs { get; init; }
        [JsonPropertyName("normalize")] public bool Normalize { get; init; }
        [JsonPropertyName("truncate")] public bool Truncate { get; init; }
    }

    private sealed class TokenizeRequest
    {
        [JsonPropertyName("inputs")] public required IReadOnlyList<string> Inputs { get; init; }
        [JsonPropertyName("add_special_tokens")] public bool AddSpecialTokens { get; init; }
    }

    private sealed class TokenInfo
    {
        [JsonPropertyName("id")] public int Id { get; init; }
    }
}
