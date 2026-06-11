using PrawoRAG.Domain.Embeddings;

namespace PrawoRAG.Tests.Fakes;

/// <summary>
/// Atrapa <see cref="IEmbeddingProvider"/> do testów bez żywego TEI.
/// Liczenie tokenów = liczba słów (deterministyczne); embedding = wektor deterministyczny z tekstu.
/// Zlicza wywołania embeddingu — kluczowe dla testów idempotencji (0 wywołań przy re-runie).
/// </summary>
public sealed class FakeEmbeddingProvider(int dimensions = 768, string modelId = "fake-embedder@v1") : IEmbeddingProvider
{
    public int PassageEmbedCalls { get; private set; }
    public int PassagesEmbedded { get; private set; }

    public string ModelId => modelId;
    public int Dimensions => dimensions;

    public Task<IReadOnlyList<float[]>> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken ct)
    {
        PassageEmbedCalls++;
        PassagesEmbedded += passages.Count;
        IReadOnlyList<float[]> result = passages.Select(Vec).ToList();
        return Task.FromResult(result);
    }

    public Task<float[]> EmbedQueryAsync(string query, CancellationToken ct) => Task.FromResult(Vec(query));

    public Task<IReadOnlyList<int>> CountTokensAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        IReadOnlyList<int> counts = texts
            .Select(t => t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length)
            .ToList();
        return Task.FromResult(counts);
    }

    private float[] Vec(string text)
    {
        var v = new float[dimensions];
        var hash = (uint)text.GetHashCode();
        for (var i = 0; i < dimensions; i++)
        {
            hash = hash * 1664525u + 1013904223u;
            v[i] = (hash % 1000) / 1000f;
        }
        return v;
    }
}
