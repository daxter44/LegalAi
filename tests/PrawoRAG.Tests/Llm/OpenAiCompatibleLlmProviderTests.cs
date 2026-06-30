using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Llm;

namespace PrawoRAG.Tests.Llm;

/// <summary>
/// Parsowanie streamingu OpenAI-compatible (Ollama/llama.cpp) bez żywego serwera — fake HttpMessageHandler.
/// Dowodzi: delty content są poprawnie sklejane, [DONE] kończy strumień, a żądanie ma właściwy kształt.
/// </summary>
public sealed class OpenAiCompatibleLlmProviderTests
{
    private sealed class StubHandler(string sse) : HttpMessageHandler
    {
        public HttpRequestMessage? Captured { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Captured = request;
            if (request.Content is not null) Body = await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private static OpenAiCompatibleLlmProvider Provider(string sse, out StubHandler handler)
    {
        handler = new StubHandler(sse);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/v1/") };
        return new OpenAiCompatibleLlmProvider(http, Options.Create(new LocalLlmOptions { Model = "bielik-test" }));
    }

    [Fact]
    public async Task Streams_content_deltas_and_stops_on_done()
    {
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"},\"finish_reason\":null}]}",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Art. \"},\"finish_reason\":null}]}",
            "data: {\"choices\":[{\"delta\":{\"content\":\"148\"},\"finish_reason\":null}]}",
            "data: {\"choices\":[{\"delta\":{\"content\":\" k.k.\"},\"finish_reason\":null}]}",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}",
            "data: [DONE]",
            "data: {\"choices\":[{\"delta\":{\"content\":\"po DONE — ignorowane\"}}]}");

        var provider = Provider(sse, out _);
        var req = new LlmRequest { Messages = [new ChatMessage(ChatRole.User, "co grozi za zabójstwo?")] };

        var sb = new StringBuilder();
        await foreach (var d in provider.StreamCompletionAsync(req, default)) sb.Append(d);

        Assert.Equal("Art. 148 k.k.", sb.ToString()); // sklejone delty; treść po [DONE] pominięta
    }

    [Fact]
    public async Task Sends_request_with_roles_model_and_stream()
    {
        var provider = Provider("data: [DONE]", out var handler);
        var req = new LlmRequest
        {
            Messages =
            [
                new ChatMessage(ChatRole.System, "Odpowiadaj wyłącznie z kontekstu."),
                new ChatMessage(ChatRole.User, "pytanie"),
            ],
            MaxTokens = 256,
        };

        await foreach (var _ in provider.StreamCompletionAsync(req, default)) { }

        Assert.EndsWith("/chat/completions", handler.Captured!.RequestUri!.AbsolutePath);
        Assert.NotNull(handler.Body);
        Assert.Contains("\"model\":\"bielik-test\"", handler.Body);
        Assert.Contains("\"role\":\"system\"", handler.Body);
        Assert.Contains("\"role\":\"user\"", handler.Body);
        Assert.Contains("\"stream\":true", handler.Body);
    }
}
