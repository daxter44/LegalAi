using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Llm;

namespace PrawoRAG.Tests.Llm;

/// <summary>
/// AJ-2 — provider czyta <c>finish_reason</c> ze strumienia SSE i zgłasza go callbackiem
/// <see cref="LlmRequest.OnFinishReason"/>. Do 2026-09-02 pole było ignorowane, więc ucięcie po
/// MaxTokens (`length`) było nieodróżnialne od normalnego końca — a to główna hipoteza pustych
/// werdyktów „?" w analizie dokumentów (model wypalił budżet na myślenie).
/// </summary>
public class OpenAiFinishReasonTests
{
    private sealed class SseHandler(string sse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            });
    }

    private static OpenAiCompatibleLlmProvider Provider(string sse) =>
        new(new HttpClient(new SseHandler(sse)) { BaseAddress = new Uri("http://fake/v1/") },
            Options.Create(new LocalLlmOptions { BaseUrl = "http://fake/v1", Model = "m" }));

    private static async Task<(string Text, string? Finish)> RunAsync(string sse)
    {
        string? finish = null;
        var req = new LlmRequest
        {
            Messages = [new(ChatRole.User, "q")],
            OnFinishReason = fr => finish = fr,
        };
        var sb = new StringBuilder();
        await foreach (var d in Provider(sse).StreamCompletionAsync(req, default)) sb.Append(d);
        return (sb.ToString(), finish);
    }

    [Fact]
    public async Task Reports_stop_on_normal_end()
    {
        var sse = """
            data: {"choices":[{"delta":{"content":"WERDYKT: OK"},"finish_reason":null}]}

            data: {"choices":[{"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        var (text, finish) = await RunAsync(sse);
        Assert.Equal("WERDYKT: OK", text);
        Assert.Equal("stop", finish);
    }

    [Fact]
    public async Task Reports_length_when_output_truncated_even_if_visible_text_is_empty()
    {
        // Całe MaxTokens zjedzone przez myślenie (flaga Google thought) → widoczna treść pusta,
        // ale finish_reason mówi DLACZEGO: to nie „model nic nie powiedział", to „nie zdążył".
        var sse = """
            data: {"choices":[{"delta":{"content":"rozważam...","extra_content":{"google":{"thought":true}}},"finish_reason":null}]}

            data: {"choices":[{"delta":{},"finish_reason":"length"}]}

            data: [DONE]

            """;
        var (text, finish) = await RunAsync(sse);
        Assert.Equal("", text);
        Assert.Equal("length", finish);
    }

    [Fact]
    public async Task No_callback_means_no_change_in_behaviour()
    {
        var sse = """
            data: {"choices":[{"delta":{"content":"x"},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        var sb = new StringBuilder();
        await foreach (var d in Provider(sse).StreamCompletionAsync(
            new LlmRequest { Messages = [new(ChatRole.User, "q")] }, default)) sb.Append(d);
        Assert.Equal("x", sb.ToString());
    }
}
