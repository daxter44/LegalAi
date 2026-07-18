using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using PrawoRAG.Embeddings;

namespace PrawoRAG.Tests.Embeddings;

/// <summary>Parsowanie odpowiedzi TEI /rerank bez żywego serwera (fake HttpMessageHandler).</summary>
public class TeiRerankerTests
{
    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.Content is not null) Body = await req.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static TeiReranker Reranker(string json, out StubHandler handler)
    {
        handler = new StubHandler(json);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8081") };
        return new TeiReranker(http, Options.Create(new RerankerOptions()));
    }

    [Fact]
    public async Task Parses_scores_and_sends_query_and_texts()
    {
        var reranker = Reranker("[{\"index\":1,\"score\":0.91},{\"index\":0,\"score\":0.12}]", out var handler);

        var res = await reranker.RerankAsync("pytanie", ["pasaż A", "pasaż B"], default);

        Assert.Equal(2, res.Count);
        Assert.Equal(1, res[0].Index);
        Assert.Equal(0.91, res[0].Score, 3);
        Assert.Equal(0, res[1].Index);
        Assert.NotNull(handler.Body);
        Assert.Contains("\"query\":\"pytanie\"", handler.Body);
        Assert.Contains("\"texts\":[", handler.Body);
    }

    [Fact]
    public async Task Empty_passages_returns_empty_without_http_call()
    {
        var reranker = Reranker("[]", out var handler);
        Assert.Empty(await reranker.RerankAsync("q", [], default));
        Assert.Null(handler.Body); // brak wywołania HTTP dla pustej listy
    }

    /// <summary>Echo handler: zwraca indeksy 0..N-1 WEWNĄTRZ paczki (jak realny TEI) + score = numer
    /// wywołania (1-based) — pozwala odróżnić, z której paczki przyszedł dany wynik.</summary>
    private sealed class BatchEchoHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<int> BatchSizes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls++;
            var body = req.Content is not null ? await req.Content.ReadAsStringAsync(ct) : "";
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var count = doc.RootElement.GetProperty("texts").GetArrayLength();
            BatchSizes.Add(count);
            var items = Enumerable.Range(0, count).Select(i => $$"""{"index":{{i}},"score":{{Calls}}.0}""");
            var json = "[" + string.Join(",", items) + "]";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact] // Zmierzone na żywym TEI: max_client_batch_size=32, przekroczenie → 422. Klient musi dzielić.
    public async Task Splits_into_batches_of_MaxBatch_and_offsets_indices_correctly()
    {
        var handler = new BatchEchoHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8081") };
        var reranker = new TeiReranker(http, Options.Create(new RerankerOptions { MaxBatch = 32 }));

        var passages = Enumerable.Range(0, 35).Select(i => $"pasaż {i}").ToList();
        var res = await reranker.RerankAsync("pytanie", passages, default);

        Assert.Equal(2, handler.Calls);              // 35 = 32 + 3 → dwa wywołania
        Assert.Equal([32, 3], handler.BatchSizes);
        Assert.Equal(35, res.Count);
        // Globalny indeks = lokalny + offset paczki; score wskazuje z której paczki przyszedł wynik.
        Assert.All(res.Where(r => r.Index < 32), r => Assert.Equal(1.0, r.Score));
        Assert.All(res.Where(r => r.Index >= 32), r => Assert.Equal(2.0, r.Score));
        Assert.Equal(Enumerable.Range(0, 35), res.Select(r => r.Index).OrderBy(i => i));
    }
}
