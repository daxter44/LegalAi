using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using PrawoRAG.Api.Services;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Tests.Fakes;

namespace PrawoRAG.Tests.Chat;

/// <summary>
/// T-TOOLLOOP (Zadanie 15 planu ROU) — narzędzie <c>szukaj_w_przepisach</c>.
///
/// Najważniejsza asercja tego pliku: gdy model NIE zawoła narzędzia (bo nie chciał albo serwer nie
/// wspiera `tools` i provider zdegradował), system MUSI zejść na ścieżkę z retrievalem — nigdy na
/// odpowiedź bez źródeł. Drugi temat: `tool_choice: required`, czyli odebranie modelowi decyzji
/// „czy potrzebuję źródeł" i zostawienie mu tylko „czego szukać".
/// </summary>
public class ToolLoopTests
{
    private static RetrievedChunk Chunk(string text) => new()
    {
        ChunkId = Guid.CreateVersion7(), DocumentId = Guid.CreateVersion7(), Text = text,
        Source = "ELI", DocType = DocTypes.Act, Title = "Ustawa", Score = 1.0, Similarity = 0.9,
    };

    private sealed class RecordingRetriever : IRetriever
    {
        public List<string> Queries { get; } = [];

        public Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, CancellationToken ct)
        {
            Queries.Add(query.Text);
            return Task.FromResult(new RetrievalResult([Chunk("Art. 415 KC…")], 0.9));
        }
    }

    private sealed class NoOpAugmenter : ITemporalAugmenter
    {
        public Task<IReadOnlyList<RetrievedChunk>> AugmentAsync(
            RetrievalQuery query, IReadOnlyList<RetrievedChunk> retrieved, CancellationToken ct)
            => Task.FromResult(retrieved);
    }

    /// <summary>LLM, który na żądaniu Z narzędziami zgłasza tool call, a potem odpowiada tekstem.</summary>
    private sealed class ToolCallingLlm(string? toolQuery, string answer = "Odpowiedź [1].") : ILlmProvider
    {
        public List<LlmRequest> Requests { get; } = [];
        public string ModelId => "fake";

        public async IAsyncEnumerable<string> StreamCompletionAsync(
            LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);

            if (request.Tools is { Count: > 0 })
            {
                if (toolQuery is not null)
                    request.OnToolCall?.Invoke(new LlmToolCall(
                        "call_1", ToolLoop.ToolName, $"{{\"zapytanie\":\"{toolQuery}\"}}"));
                yield break; // żądanie narzędziowe nie produkuje widocznego tekstu
            }

            yield return answer;
            await Task.CompletedTask;
        }
    }

    private static ChatService Service(IRetriever retriever, ILlmProvider llm, bool toolCalling) =>
        new(retriever, new NoOpAugmenter(), llm,
            Options.Create(new RetrievalOptions { ToolCallingEnabled = toolCalling, MaxToolCalls = 2 }),
            new FakeEmbeddingProvider(), Options.Create(new DocumentsOptions { Enabled = false }));

    private static async Task<List<ChatEvent>> Drain(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events) list.Add(e);
        return list;
    }

    [Fact] // Zapytanie SFORMULOWANE PRZEZ MODEL zasila retrieval (a nie pytanie uzytkownika).
    public async Task Model_query_feeds_retrieval()
    {
        var retriever = new RecordingRetriever();
        var llm = new ToolCallingLlm("odpowiedzialność deliktowa art. 415");

        var events = await Drain(Service(retriever, llm, toolCalling: true)
            .AskAsync("kto płaci za szkodę?", [], null, default));

        Assert.Contains("odpowiedzialność deliktowa art. 415", retriever.Queries);
        Assert.Contains(events, e => e is RetryingRetrievalEvent);
        Assert.Contains(events, e => e is SourcesEvent);
        Assert.Contains(events, e => e is TokenEvent);
    }

    [Fact] // tool_choice=required na zadaniu narzedziowym: model nie ma prawa odpowiedziec bez bazy.
    public async Task First_request_forces_tool_call()
    {
        var llm = new ToolCallingLlm("cokolwiek");
        await Drain(Service(new RecordingRetriever(), llm, toolCalling: true)
            .AskAsync("pytanie", [], null, default));

        var toolRequest = llm.Requests.First(r => r.Tools is { Count: > 0 });
        Assert.Equal("required", toolRequest.ToolChoice);
        Assert.Equal(ToolLoop.ToolName, Assert.Single(toolRequest.Tools!).Name);
        // Regula R2: zadanie formulujace wywolanie nie potrzebuje rozumowania.
        Assert.Equal(256, toolRequest.MaxTokens);
        Assert.Equal(0, toolRequest.Temperature);
    }

    [Fact] // BRAK wywolania narzedzia (model odmowil albo serwer zdegradowal) => retrieval z pytaniem
           // uzytkownika. To najwazniejszy test: brak wsparcia NIE MOZE dac odpowiedzi bez zrodel.
    public async Task No_tool_call_degrades_to_user_question_retrieval()
    {
        var retriever = new RecordingRetriever();
        var llm = new ToolCallingLlm(toolQuery: null);

        var events = await Drain(Service(retriever, llm, toolCalling: true)
            .AskAsync("kto płaci za szkodę?", [], null, default));

        Assert.Contains("kto płaci za szkodę?", retriever.Queries);   // retrieval WYKONANY
        Assert.Contains(events, e => e is SourcesEvent);               // ze źródłami
        Assert.DoesNotContain(events, e => e is NoRetrievalEvent);     // nigdy bez bazy
        Assert.Contains(events, e => e is TokenEvent);
    }

    [Fact] // FLAGA OFF (domyslna, regula R1) => zero zadania narzedziowego, jedno wywolanie modelu.
    public async Task Flag_off_means_single_model_call()
    {
        var retriever = new RecordingRetriever();
        var llm = new ToolCallingLlm("nieużywane");

        await Drain(Service(retriever, llm, toolCalling: false)
            .AskAsync("kto płaci za szkodę?", [], null, default));

        Assert.Single(llm.Requests);                                   // JEDNO wywołanie Gemmy
        Assert.All(llm.Requests, r => Assert.Null(r.Tools));
        Assert.Contains("kto płaci za szkodę?", retriever.Queries);
    }

    [Fact] // Bramki dzialaja na wyniku narzedzia bez zmian - kontekst z narzedzia przechodzi ta sama
           // sciezke walidacji co kontekst klasyczny.
    public async Task Gates_still_apply_to_tool_result()
    {
        var retriever = new RecordingRetriever();
        var llm = new ToolCallingLlm("zapytanie", answer: "Zgodnie z art. 415 [1] odpowiadasz.");

        var events = await Drain(Service(retriever, llm, toolCalling: true)
            .AskAsync("pytanie", [], null, default));

        var done = Assert.IsType<DoneEvent>(events[^1]);
        Assert.NotNull(done.Check);                                    // walidacja cytatów wykonana
        Assert.True(done.Check!.IsClean);
    }

    // --- HISTORIA A KOSZT RETRIEVALU (fix 2026-08-27) ---
    // Sklejka kontekstowa w FollowUpSelector istnieje TYLKO dlatego, że surowe dopytanie nie niesie
    // treści. Zapytanie napisane przez model, który widział rozmowę (prompt narzędziowy dostaje
    // historię), już ją niesie — więc sklejanie go po raz drugi to podwójny embedding + SQL +
    // reranker za pogorszenie tekstu, który model właśnie napisał.

    [Fact]
    public async Task Tool_call_with_history_runs_single_retrieval()
    {
        var retriever = new RecordingRetriever();
        var llm = new ToolCallingLlm("solidarność dłużników art. 367 § 2 KPC");
        ChatTurn[] history = [new("co mówi art. 367 KPC?", "Solidarność dłużników.")];

        await Drain(Service(retriever, llm, toolCalling: true)
            .AskAsync("a co z § 2?", history, null, default));

        // JEDEN przebieg, i to na zapytaniu modelu — nie dwa (surowe + sklejka z historią).
        Assert.Equal(["solidarność dłużników art. 367 § 2 KPC"], retriever.Queries);
    }

    [Fact] // Model NIE zawolal narzedzia => historia zostaje, czyli dzisiejsza sciezka follow-upu
           // (podwojny retrieval i wybor wariantu) dziala bez zmian. Fix nie moze jej zabrac.
    public async Task No_tool_call_keeps_contextual_follow_up_path()
    {
        var retriever = new RecordingRetriever();
        var llm = new ToolCallingLlm(toolQuery: null);
        ChatTurn[] history = [new("co mówi art. 367 KPC?", "Solidarność dłużników.")];

        await Drain(Service(retriever, llm, toolCalling: true)
            .AskAsync("a co z § 2?", history, null, default));

        Assert.Equal(2, retriever.Queries.Count);                       // surowe + kontekstowe
        Assert.Contains("a co z § 2?", retriever.Queries);
        Assert.Contains(retriever.Queries, q => q.Contains("co mówi art. 367 KPC?"));
    }

    // --- Czysta funkcja pętli (bez ChatService) ---

    private sealed class ArgsLlm(string argumentsJson, string toolName = ToolLoop.ToolName) : ILlmProvider
    {
        public string ModelId => "fake";

        public async IAsyncEnumerable<string> StreamCompletionAsync(
            LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            request.OnToolCall?.Invoke(new LlmToolCall("id", toolName, argumentsJson));
            yield break;
        }
    }

    private static Task<ToolLoop.ToolLoopResult> Collect(ILlmProvider llm, int max = 2) =>
        ToolLoop.CollectQueriesAsync(llm, [new ChatMessage(ChatRole.User, "pytanie")], max, default);

    [Theory] // Model bywa niedokladny - zamiast rzucac, degradujemy do bezwarunkowego retrievalu.
    [InlineData("")]                                  // brak argumentów
    [InlineData("{")]                                 // urwany JSON (limit tokenów)
    [InlineData("\"tekst\"")]                         // nie obiekt
    [InlineData("{\"zapytanie\":\"\"}")]              // puste zapytanie
    [InlineData("{\"a\":\"x\",\"b\":\"y\"}")]          // dwa pola tekstowe → niejednoznaczne
    public async Task Malformed_arguments_mean_no_tool_call(string args)
    {
        var result = await Collect(new ArgsLlm(args));
        Assert.True(result.NoToolCall);
    }

    [Fact] // Fallback: model nazwal pole inaczej, ale zapytanie jest oczywiste (jedna wartosc tekstowa).
    public async Task Single_string_field_is_accepted_as_query()
    {
        var result = await Collect(new ArgsLlm("{\"query\":\"kara umowna\"}"));

        Assert.False(result.NoToolCall);
        Assert.Equal("kara umowna", result.Queries[0]);
    }

    [Fact] // Wywolanie INNEGO narzedzia ignorowane - nie zgadujemy, co model mial na mysli.
    public async Task Unknown_tool_name_is_ignored()
    {
        var result = await Collect(new ArgsLlm("{\"zapytanie\":\"x\"}", toolName: "cos_innego"));
        Assert.True(result.NoToolCall);
    }
}
