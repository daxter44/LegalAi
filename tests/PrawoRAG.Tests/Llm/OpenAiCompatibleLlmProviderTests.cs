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
    public async Task Reports_real_usage_from_final_chunk()
    {
        // Finalny chunk stream_options.include_usage: puste choices + usage — nie może psuć tekstu.
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Odpowiedź.\"},\"finish_reason\":null}]}",
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":5214,\"completion_tokens\":487}}",
            "data: [DONE]");
        var provider = Provider(sse, out var handler);
        LlmUsage? usage = null;
        var req = new LlmRequest
        {
            Messages = [new ChatMessage(ChatRole.User, "pytanie")],
            OnUsage = u => usage = u,
        };

        var sb = new StringBuilder();
        await foreach (var d in provider.StreamCompletionAsync(req, default)) sb.Append(d);

        Assert.Equal("Odpowiedź.", sb.ToString());
        Assert.Equal(new LlmUsage(5214, 487, Estimated: false), usage);
        Assert.Contains("\"include_usage\":true", handler.Body); // żądanie prosi o usage
    }

    [Fact]
    public async Task Falls_back_to_estimate_when_server_reports_no_usage()
    {
        // Serwer bez wsparcia stream_options (stary llama.cpp) → szacunek ze znaków, JAWNIE oznaczony.
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"12345678\"},\"finish_reason\":null}]}", // 8 znaków wyjścia
            "data: [DONE]");
        var provider = Provider(sse, out _);
        LlmUsage? usage = null;
        var req = new LlmRequest
        {
            Messages = [new ChatMessage(ChatRole.User, new string('x', 40))], // 40 znaków wejścia
            OnUsage = u => usage = u,
        };

        await foreach (var _ in provider.StreamCompletionAsync(req, default)) { }

        Assert.NotNull(usage);
        Assert.True(usage!.Estimated);
        Assert.Equal(10, usage.InputTokens);  // 40/4
        Assert.Equal(2, usage.OutputTokens);  // 8/4
    }

    [Fact]
    public async Task No_callback_means_no_usage_work()
    {
        // Eval/testy nie ustawiają OnUsage — strumień działa jak dotąd, zero wyjątków.
        var provider = Provider("data: [DONE]", out _);
        var req = new LlmRequest { Messages = [new ChatMessage(ChatRole.User, "q")] };

        await foreach (var _ in provider.StreamCompletionAsync(req, default)) { }
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
