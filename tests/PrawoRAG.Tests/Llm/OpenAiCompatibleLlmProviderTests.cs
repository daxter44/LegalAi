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

    // --- Zadanie 1: delty rozumowania płyną W TRAKCIE strumienia, nie po nim ---

    /// <summary>SSE z rozumowaniem Gemmy: delty z flagą google.thought, potem widoczna treść.</summary>
    private const string ThinkingSse = """
        data: {"choices":[{"delta":{"content":"<thought>Sprawdzam art. ","extra_content":{"google":{"thought":true}}}}]}
        data: {"choices":[{"delta":{"content":"37 ustawy","extra_content":{"google":{"thought":true}}}}]}
        data: {"choices":[{"delta":{"content":"</thought>Odpowiedź: tak [1]."}}]}
        data: [DONE]
        """;

    [Fact] // Delty rozumowania musza dotrzec PRZED zakonczeniem strumienia — inaczej UI nie ma co pokazac.
    public async Task Reasoning_deltas_arrive_during_stream_not_after()
    {
        var provider = Provider(ThinkingSse, out _);
        var reasoningDeltas = new List<string>();
        var visibleSoFar = new StringBuilder();
        // Znacznik: ile delt rozumowania widzielismy, gdy przyszla PIERWSZA widoczna delta.
        var deltasBeforeFirstVisible = -1;

        var req = new LlmRequest
        {
            Messages = [new ChatMessage(ChatRole.User, "pytanie")],
            OnReasoningDelta = d => reasoningDeltas.Add(d),
        };

        await foreach (var visible in provider.StreamCompletionAsync(req, default))
        {
            if (deltasBeforeFirstVisible < 0) deltasBeforeFirstVisible = reasoningDeltas.Count;
            visibleSoFar.Append(visible);
        }

        Assert.Equal("Odpowiedź: tak [1].", visibleSoFar.ToString());
        Assert.Equal(2, deltasBeforeFirstVisible); // oba fragmenty myslenia PRZED pierwszym widocznym tokenem
        Assert.Equal("Sprawdzam art. 37 ustawy", string.Concat(reasoningDeltas));
    }

    [Fact] // Rownowaznosc na poziomie providera: suma delt == to, co dostaje OnReasoning (zapis do historii).
    public async Task Reasoning_deltas_equal_final_reasoning()
    {
        var provider = Provider(ThinkingSse, out _);
        var deltas = new List<string>();
        string? whole = null;

        var req = new LlmRequest
        {
            Messages = [new ChatMessage(ChatRole.User, "pytanie")],
            OnReasoningDelta = deltas.Add,
            OnReasoning = r => whole = r,
        };

        await foreach (var _ in provider.StreamCompletionAsync(req, default)) { }

        Assert.NotNull(whole);
        Assert.Equal(whole, string.Concat(deltas));
    }

    [Fact] // Brak callbacku = dzisiejsze zachowanie bajt w bajt (Eval, testy, Claude/Bielik bez myslenia).
    public async Task Without_delta_callback_visible_stream_is_unchanged()
    {
        var provider = Provider(ThinkingSse, out _);
        var req = new LlmRequest { Messages = [new ChatMessage(ChatRole.User, "pytanie")] };

        var sb = new StringBuilder();
        await foreach (var d in provider.StreamCompletionAsync(req, default)) sb.Append(d);

        Assert.Equal("Odpowiedź: tak [1].", sb.ToString());
    }

    // --- Zadanie 14: tools / tool_choice + degradacja, gdy serwer ich nie zna ---

    private static readonly LlmTool SearchTool = new(
        "szukaj_w_przepisach",
        "Szuka przepisów i orzeczeń w bazie prawa polskiego.",
        """{"type":"object","properties":{"zapytanie":{"type":"string"}},"required":["zapytanie"]}""");

    private static LlmRequest ToolRequest(string choice = "required") => new()
    {
        Messages = [new ChatMessage(ChatRole.User, "czy ponoszę odpowiedzialność?")],
        Tools = [SearchTool],
        ToolChoice = choice,
    };

    [Fact] // Zadanie zawiera tools i tool_choice w kształcie OpenAI-compat.
    public async Task Request_carries_tools_and_tool_choice()
    {
        var provider = Provider("data: [DONE]", out var handler);
        await foreach (var _ in provider.StreamCompletionAsync(ToolRequest(), default)) { }

        Assert.Contains("\"tools\":", handler.Body);
        Assert.Contains("\"tool_choice\":\"required\"", handler.Body);
        Assert.Contains("szukaj_w_przepisach", handler.Body);
        Assert.Contains("\"type\":\"function\"", handler.Body);
        Assert.Contains("\"zapytanie\"", handler.Body);   // JSON Schema przekazany bez interpretacji
    }

    [Fact] // ROWNOWAZNOSC: bez narzedzi cialo zadania nie ma tych pol wcale (zero zmian dla dzisiejszych
           // wywolan - eval, analiza dokumentow, Claude).
    public async Task Request_without_tools_is_unchanged()
    {
        var provider = Provider("data: [DONE]", out var handler);
        var req = new LlmRequest { Messages = [new ChatMessage(ChatRole.User, "pytanie")] };

        await foreach (var _ in provider.StreamCompletionAsync(req, default)) { }

        Assert.DoesNotContain("tools", handler.Body);
        Assert.DoesNotContain("tool_choice", handler.Body);
    }

    [Fact] // Delty tool_calls skladane w calosc: id i name z pierwszej, arguments z kolejnych.
           // Bez akumulacji dostalibysmy pokawalkowany, niesparsowalny JSON.
    public async Task Tool_call_deltas_are_accumulated()
    {
        var sse = string.Join("\n",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"szukaj_w_przepisach","arguments":""}}]}}]}""",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"zapyt"}}]}}]}""",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"anie\":\"odpowiedzialność deliktowa\"}"}}]}}]}""",
            "data: [DONE]");

        var provider = Provider(sse, out _);
        var calls = new List<LlmToolCall>();
        var req = ToolRequest() with { OnToolCall = calls.Add };

        await foreach (var _ in provider.StreamCompletionAsync(req, default)) { }

        var call = Assert.Single(calls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("szukaj_w_przepisach", call.Name);
        Assert.Equal("""{"zapytanie":"odpowiedzialność deliktowa"}""", call.ArgumentsJson);
    }

    [Fact] // Dwa rownolegle wywolania (rozny index) nie mieszaja sie ze soba.
    public async Task Parallel_tool_calls_are_kept_separate()
    {
        var sse = string.Join("\n",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"a","function":{"name":"szukaj_w_przepisach","arguments":"{\"zapytanie\":\"kara umowna\"}"}}]}}]}""",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":1,"id":"b","function":{"name":"szukaj_w_przepisach","arguments":"{\"zapytanie\":\"odsetki\"}"}}]}}]}""",
            "data: [DONE]");

        var provider = Provider(sse, out _);
        var calls = new List<LlmToolCall>();
        await foreach (var _ in provider.StreamCompletionAsync(
            ToolRequest() with { OnToolCall = calls.Add }, default)) { }

        Assert.Equal(2, calls.Count);
        Assert.Contains(calls, c => c.Id == "a" && c.ArgumentsJson.Contains("kara umowna"));
        Assert.Contains(calls, c => c.Id == "b" && c.ArgumentsJson.Contains("odsetki"));
    }

    /// <summary>Handler odrzucający PIERWSZE żądanie 400, a kolejne obsługujący normalnie —
    /// odtwarza serwer, który nie zna pól `tools`/`tool_choice`.</summary>
    private sealed class RejectToolsHandler(string sse) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Bodies.Add(body);

            if (body.Contains("\"tools\""))
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("unknown field: tools"),
                };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    [Fact] // DEGRADACJA: 4xx na zadaniu z narzedziami => ponowienie BEZ narzedzi i normalna odpowiedz.
           // Gemma bywa serwowana stosami, ktore tych pol nie znaja - to musi dzialac, nie wywalac sie.
    public async Task Server_rejecting_tools_falls_back_without_them()
    {
        var handler = new RejectToolsHandler(
            "data: {\"choices\":[{\"delta\":{\"content\":\"Odpowiedź [1].\"}}]}\ndata: [DONE]");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/v1/") };
        var provider = new OpenAiCompatibleLlmProvider(http, Options.Create(new LocalLlmOptions()));

        var sb = new StringBuilder();
        await foreach (var d in provider.StreamCompletionAsync(ToolRequest(), default)) sb.Append(d);

        Assert.Equal("Odpowiedź [1].", sb.ToString());          // odpowiedź powstała
        Assert.Equal(2, handler.Bodies.Count);                    // próba z narzędziami + bez
        Assert.Contains("\"tools\"", handler.Bodies[0]);
        Assert.DoesNotContain("\"tools\"", handler.Bodies[1]);
    }

    [Fact] // Po odrzuceniu NIE probujemy wiecej w tym procesie - kolejne zadanie leci od razu bez narzedzi
           // (jeden round-trip, nie dwa).
    public async Task Tools_are_not_retried_after_rejection()
    {
        var handler = new RejectToolsHandler(
            "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\ndata: [DONE]");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/v1/") };
        var provider = new OpenAiCompatibleLlmProvider(http, Options.Create(new LocalLlmOptions()));

        await foreach (var _ in provider.StreamCompletionAsync(ToolRequest(), default)) { }
        var afterFirst = handler.Bodies.Count;

        await foreach (var _ in provider.StreamCompletionAsync(ToolRequest(), default)) { }

        Assert.Equal(afterFirst + 1, handler.Bodies.Count);       // tylko JEDNO żądanie więcej
        Assert.DoesNotContain("\"tools\"", handler.Bodies[^1]);
    }

    [Fact] // 5xx to NIE brak wsparcia dla narzedzi - realny blad musi propagowac, nie byc maskowany
           // cicha degradacja (inaczej ukrylibysmy awarie serwera).
    public async Task Server_error_still_throws()
    {
        var failing = new HttpClient(new AlwaysFailHandler(HttpStatusCode.InternalServerError))
        {
            BaseAddress = new Uri("http://localhost:11434/v1/"),
        };
        var provider = new OpenAiCompatibleLlmProvider(failing, Options.Create(new LocalLlmOptions()));

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in provider.StreamCompletionAsync(ToolRequest(), default)) { }
        });
    }

    private sealed class AlwaysFailHandler(HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent("boom") });
    }
}
