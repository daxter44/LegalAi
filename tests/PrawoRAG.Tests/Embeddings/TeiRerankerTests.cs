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
}
