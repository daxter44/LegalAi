using System.Runtime.CompilerServices;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Llm;

namespace PrawoRAG.Tests.Llm;

/// <summary>
/// T-ROUTER (Zadanie 7 planu ROU) — router intencji na modelu pomocniczym.
///
/// Rdzeń tych testów to KONTRAKT FAIL-SAFE: router nie ma prawa rzucić wyjątku ani zwrócić
/// „pomiń bazę" przy jakiejkolwiek niepewności. Powód jest asymetryczny — small-talk wpuszczony
/// do retrievalu kosztuje tylko czas, ale pytanie prawne uznane za small-talk daje odpowiedź BEZ
/// źródeł, czyli łamie rdzeń wartości produktu. Dlatego każdy scenariusz awarii ma tu własny test.
/// </summary>
public class AuxIntentRouterTests
{
    /// <summary>LLM zwracający ustaloną odpowiedź albo rzucający ustalony wyjątek.</summary>
    private sealed class ScriptedLlm(string? response, Exception? throws = null) : ILlmProvider
    {
        public string ModelId => "aux-test";

        public async IAsyncEnumerable<string> StreamCompletionAsync(
            LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Captured = request;
            if (throws is not null) throw throws;
            // Strumień po kawałkach — jak realny provider (JSON bywa rozcięty między deltami).
            foreach (var part in Chunk(response ?? "")) { yield return part; await Task.Yield(); }
        }

        public LlmRequest? Captured { get; private set; }

        private static IEnumerable<string> Chunk(string s)
        {
            for (var i = 0; i < s.Length; i += 7) yield return s[i..Math.Min(i + 7, s.Length)];
        }
    }

    private static AuxIntentRouter Router(string? response, Exception? throws = null) =>
        new(new ScriptedLlm(response, throws));

    [Fact] // Poprawny JSON: pytanie prawne => do bazy, z proponowanym zapytaniem.
    public async Task Parses_needs_law_true()
    {
        var d = await Router("""{"potrzebne_przepisy": true, "zapytanie": "rozwód bez orzekania o winie", "uzasadnienie": "pytanie o prawo rodzinne"}""")
            .RouteAsync("jak z rozwodem bez winy?", [], default);

        Assert.True(d.PotrzebnePrzepisy);
        Assert.Equal("rozwód bez orzekania o winie", d.Zapytanie);
    }

    [Fact] // Poprawny JSON: small-talk => bez bazy. Jedyna sciezka, ktora pomija retrieval.
    public async Task Parses_needs_law_false()
    {
        var d = await Router("""{"potrzebne_przepisy": false, "zapytanie": "", "uzasadnienie": "powitanie"}""")
            .RouteAsync("siema", [], default);

        Assert.False(d.PotrzebnePrzepisy);
        Assert.Null(d.Zapytanie);
    }

    [Fact] // Model gadatliwy (tekst wokol JSON-a) - i tak parsujemy, bo to typowe dla malych modeli.
    public async Task Parses_json_wrapped_in_prose()
    {
        var d = await Router("""Oczywiście! Oto klasyfikacja: {"potrzebne_przepisy": false, "uzasadnienie": "powitanie"} Mam nadzieję, że pomogłem.""")
            .RouteAsync("cześć", [], default);

        Assert.False(d.PotrzebnePrzepisy);
    }

    [Theory] // KAZDA forma niepewnosci => retrieval. To jest caly kontrakt tej klasy.
    [InlineData("")]                                              // pusta odpowiedź
    [InlineData("nie wiem")]                                      // zero JSON-a
    [InlineData("{")]                                             // urwany JSON
    [InlineData("""{"potrzebne_przepisy": "false"}""")]            // zły typ (string, nie bool)
    [InlineData("""{"potrzebne_przepisy": null}""")]               // null
    [InlineData("""{"uzasadnienie": "brak pola decyzji"}""")]      // brak pola decyzji
    [InlineData("""{"potrzebne_przepisy": maybe}""")]              // niepoprawny JSON
    public async Task Any_uncertainty_falls_back_to_retrieval(string response)
    {
        var d = await Router(response).RouteAsync("pytanie", [], default);
        Assert.True(d.PotrzebnePrzepisy);
    }

    [Fact] // Awaria providera (brak serwera, 5xx, timeout klienta) => retrieval, bez wyjatku na zewnatrz.
    public async Task Provider_failure_falls_back_to_retrieval()
    {
        var d = await Router(null, new HttpRequestException("connection refused"))
            .RouteAsync("pytanie", [], default);

        Assert.True(d.PotrzebnePrzepisy);
        Assert.Contains("awaria", d.Uzasadnienie);
    }

    [Fact] // Timeout klienta pomocniczego (TaskCanceledException bez zadania uzytkownika) => retrieval.
    public async Task Client_timeout_falls_back_to_retrieval()
    {
        var d = await Router(null, new TaskCanceledException("timeout"))
            .RouteAsync("pytanie", [], default);

        Assert.True(d.PotrzebnePrzepisy);
    }

    [Fact] // Anulowanie PRZEZ UZYTKOWNIKA to nie awaria routera - musi propagowac, nie byc tlumione.
    public async Task User_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Router(null, new OperationCanceledException()).RouteAsync("pytanie", [], cts.Token));
    }

    [Fact] // Puste pytanie => retrieval (dzisiejsze zachowanie), bez wolania modelu.
    public async Task Empty_question_falls_back_without_calling_model()
    {
        var llm = new ScriptedLlm("""{"potrzebne_przepisy": false}""");
        var d = await new AuxIntentRouter(llm).RouteAsync("   ", [], default);

        Assert.True(d.PotrzebnePrzepisy);
        Assert.Null(llm.Captured); // model NIE wolany — oszczędzone wywołanie
    }

    [Fact] // Router MUSI widziec poprzednia ture: follow-up ("a co z § 2?") sam wyglada jak pogawedka,
           // a jego zrouterowanie na small-talk to najgorszy mozliwy blad (odpowiedz prawna bez zrodel).
    public async Task Follow_up_receives_previous_question_in_context()
    {
        var llm = new ScriptedLlm("""{"potrzebne_przepisy": true, "zapytanie": "x"}""");
        await new AuxIntentRouter(llm).RouteAsync(
            "a co z § 2?", [new ChatTurn("jakie są przesłanki rozwodu?", "Rozwód wymaga…")], default);

        var userMessage = llm.Captured!.Messages.Last(m => m.Role == ChatRole.User).Content;
        Assert.Contains("przesłanki rozwodu", userMessage);
        Assert.Contains("§ 2", userMessage);
    }

    [Fact] // Determinizm: klasyfikacja, nie tworczosc.
    public async Task Uses_zero_temperature()
    {
        var llm = new ScriptedLlm("""{"potrzebne_przepisy": true}""");
        await new AuxIntentRouter(llm).RouteAsync("pytanie", [], default);

        Assert.Equal(0, llm.Captured!.Temperature);
    }
}
