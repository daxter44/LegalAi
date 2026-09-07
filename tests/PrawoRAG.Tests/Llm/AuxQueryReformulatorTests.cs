using System.Runtime.CompilerServices;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Llm;

namespace PrawoRAG.Tests.Llm;

/// <summary>
/// T-REFORM (Zadanie 11 planu ROU) — przeformułowanie zapytania na terminologię ustawową dla
/// DRUGIEJ rundy retrievalu.
///
/// Najważniejsza asercja tego pliku nie dotyczy jakości przeformułowania (tego nie da się sprawdzić
/// bez modelu), a tego, KIEDY mechanizm zwraca null. Pipeline retrievalu jest DETERMINISTYCZNY,
/// więc powtórzenie tego samego zapytania to gwarantowana strata ~40 s bez żadnej szansy na inny
/// wynik — a przy awarii modelu pomocniczego musimy wrócić do dzisiejszego zachowania, nie wywalić
/// czatu.
/// </summary>
public class AuxQueryReformulatorTests
{
    private sealed class ScriptedLlm(string? response, Exception? throws = null) : ILlmProvider
    {
        public string ModelId => "aux-test";

        public async IAsyncEnumerable<string> StreamCompletionAsync(
            LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Captured = request;
            if (throws is not null) throw throws;
            yield return response ?? "";
            await Task.CompletedTask;
        }

        public LlmRequest? Captured { get; private set; }
    }

    private static AuxQueryReformulator Reformulator(string? response, Exception? throws = null) =>
        new(new ScriptedLlm(response, throws));

    [Fact] // Sciezka szczesliwa: model oddaje samo zapytanie w terminologii ustawowej.
    public async Task Returns_reformulated_query()
    {
        var result = await Reformulator("zgłoszenie naruszenia ochrony danych Prezesowi UODO")
            .ReformulateAsync("komu zgłosić wyciek danych do głównego inspektora?", [], default);

        Assert.Equal("zgłoszenie naruszenia ochrony danych Prezesowi UODO", result);
    }

    [Fact] // Model gadatliwy: bierzemy pierwsza linie, bo prompt kaze oddac samo zapytanie.
    public async Task Takes_first_line_when_model_adds_commentary()
    {
        var result = await Reformulator(
                "rozwiązanie umowy o pracę w okresie niezdolności do pracy\n\nMam nadzieję, że pomogłem!")
            .ReformulateAsync("zwolnienie na chorobowym", [], default);

        Assert.Equal("rozwiązanie umowy o pracę w okresie niezdolności do pracy", result);
    }

    [Fact] // Cudzyslowy wokol zapytania sa obcinane - inaczej trafialyby do BM25 jako tresc.
    public async Task Strips_surrounding_quotes()
    {
        var result = await Reformulator("\"kara umowna za opóźnienie\"")
            .ReformulateAsync("kara za spóźnienie", [], default);

        Assert.Equal("kara umowna za opóźnienie", result);
    }

    [Theory] // Null wszedzie, gdzie druga runda nie mialaby sensu albo nie ma czego uzyc.
    [InlineData("")]           // puste wyjście
    [InlineData("   ")]        // same białe znaki
    [InlineData("BRAK")]       // model sam mówi, że nie ma innego wariantu
    [InlineData("brak")]       // wielkość liter bez znaczenia
    public async Task Returns_null_for_unusable_output(string response)
    {
        var result = await Reformulator(response).ReformulateAsync("pytanie", [], default);
        Assert.Null(result);
    }

    [Theory] // Wyjscie ROWNOWAZNE wejsciu => null. Pipeline jest deterministyczny, wiec druga runda
             // z tym samym zapytaniem to gwarantowana strata czasu bez szansy na inny wynik.
    [InlineData("kara umowna za opóźnienie")]        // identyczne
    [InlineData("Kara Umowna Za Opóźnienie")]        // inna wielkość liter
    [InlineData("  kara   umowna  za opóźnienie  ")] // inne białe znaki
    [InlineData("kara umowna za opóźnienie?")]       // dodany znak zapytania
    public async Task Returns_null_when_output_equals_input(string response)
    {
        var result = await Reformulator(response)
            .ReformulateAsync("kara umowna za opóźnienie", [], default);

        Assert.Null(result);
    }

    [Fact] // Awaria providera (brak serwera, 5xx) => null, BEZ wyjatku - inaczej wywalilaby czat.
    public async Task Provider_failure_returns_null()
    {
        var result = await Reformulator(null, new HttpRequestException("connection refused"))
            .ReformulateAsync("pytanie", [], default);

        Assert.Null(result);
    }

    [Fact] // Timeout klienta pomocniczego => null (skonczony timeout to projekt, nie awaria).
    public async Task Client_timeout_returns_null()
    {
        var result = await Reformulator(null, new TaskCanceledException("timeout"))
            .ReformulateAsync("pytanie", [], default);

        Assert.Null(result);
    }

    [Fact] // Anulowanie PRZEZ UZYTKOWNIKA propaguje - to nie awaria mechanizmu.
    public async Task User_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Reformulator(null, new OperationCanceledException()).ReformulateAsync("pytanie", [], cts.Token));
    }

    [Fact] // Puste pytanie nie wola modelu wcale.
    public async Task Empty_question_does_not_call_model()
    {
        var llm = new ScriptedLlm("cokolwiek");
        var result = await new AuxQueryReformulator(llm).ReformulateAsync("  ", [], default);

        Assert.Null(result);
        Assert.Null(llm.Captured);
    }

    [Fact] // Determinizm - to wyszukiwanie, nie tworczosc.
    public async Task Uses_zero_temperature()
    {
        var llm = new ScriptedLlm("inne zapytanie");
        await new AuxQueryReformulator(llm).ReformulateAsync("pytanie", [], default);

        Assert.Equal(0, llm.Captured!.Temperature);
    }

    // --- HISTORIA W PROMPCIE (fix 2026-08-27) ---

    [Fact] // Rozmowa DOCHODZI do modelu - bez niej „a co z § 2?" nie ma tematu do przelozenia.
    public async Task History_reaches_the_prompt()
    {
        var llm = new ScriptedLlm("solidarność dłużników art. 367 § 2 KPC");
        ChatTurn[] history = [new("co mówi art. 367 KPC?", "Art. 367 KPC dotyczy solidarności [1].")];

        await new AuxQueryReformulator(llm).ReformulateAsync("a co z § 2?", history, default);

        var user = llm.Captured!.Messages.Single(m => m.Role == ChatRole.User).Content;
        Assert.Contains("co mówi art. 367 KPC?", user);
        Assert.Contains("solidarności", user);
        Assert.Contains("a co z § 2?", user);
        Assert.DoesNotContain("[1]", user);   // markery tamtej tury nic tu nie znaczą
        // Historia jako BLOK TEKSTU, nie rola Assistant: model ma przepisać zapytanie,
        // a w roli Assistant potrafi zacząć odpowiadać na pytanie.
        Assert.DoesNotContain(llm.Captured.Messages, m => m.Role == ChatRole.Assistant);
    }

    [Fact] // Pusta historia = prompt dokladnie jak dotad (pytanie samo, bez naglowkow rozmowy).
    public async Task Empty_history_keeps_bare_question()
    {
        var llm = new ScriptedLlm("inne zapytanie");
        await new AuxQueryReformulator(llm).ReformulateAsync("kara za spóźnienie", [], default);

        var user = llm.Captured!.Messages.Single(m => m.Role == ChatRole.User).Content;
        Assert.Equal("kara za spóźnienie", user);
    }
}
