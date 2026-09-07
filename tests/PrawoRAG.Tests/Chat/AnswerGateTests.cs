using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using PrawoRAG.Api.Services;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Llm.Grounding;
using PrawoRAG.Tests.Fakes;

namespace PrawoRAG.Tests.Chat;

/// <summary>
/// T-GATE (Zadanie 10 planu ROU) — bramka anty-fabrykacji.
///
/// Kontekst: <see cref="CitationValidator"/> od dawna wykrywał artykuły i sygnatury nieobecne
/// w kontekście, ale <c>IsClean</c> napędzał WYŁĄCZNIE badge ⚠ — odpowiedź z wymyślonym artykułem
/// wychodziła do użytkownika. Te testy pilnują, że teraz nie wychodzi, ORAZ — równie ważne — że
/// bramka nie zawraca odpowiedzi poprawnych (fałszywy alarm zamienia dobrą odpowiedź na odmowę,
/// co byłoby porażką; próg zabicia w planie to >10%).
/// </summary>
public class AnswerGateTests
{
    private const string ContextText = "Art. 415 KC. Kto z winy swojej wyrządził szkodę, obowiązany jest…";

    private sealed class FixedRetriever(double similarity) : IRetriever
    {
        public Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, CancellationToken ct)
        {
            var chunk = new RetrievedChunk
            {
                ChunkId = Guid.CreateVersion7(), DocumentId = Guid.CreateVersion7(),
                Text = ContextText, Source = "ELI", DocType = DocTypes.Act,
                Title = "Kodeks cywilny", Score = 1.0, Similarity = similarity,
            };
            return Task.FromResult(new RetrievalResult([chunk], similarity));
        }
    }

    private sealed class NoOpAugmenter : ITemporalAugmenter
    {
        public Task<IReadOnlyList<RetrievedChunk>> AugmentAsync(
            RetrievalQuery query, IReadOnlyList<RetrievedChunk> retrieved, CancellationToken ct)
            => Task.FromResult(retrieved);
    }

    /// <summary>LLM oddający kolejno zaplanowane odpowiedzi — pozwala odegrać „brudna, potem czysta".</summary>
    private sealed class SequenceLlm(params string[] answers) : ILlmProvider
    {
        private int _call;
        public List<LlmRequest> Requests { get; } = [];
        public string ModelId => "fake";

        public async IAsyncEnumerable<string> StreamCompletionAsync(
            LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            yield return answers[Math.Min(_call++, answers.Length - 1)];
            await Task.CompletedTask;
        }
    }

    private static ChatService Service(ILlmProvider llm, bool gateEnabled = true) =>
        new(new FixedRetriever(0.9), new NoOpAugmenter(), llm,
            Options.Create(new RetrievalOptions()), new FakeEmbeddingProvider(),
            Options.Create(new DocumentsOptions { Enabled = false }), router: null,
            Options.Create(new GroundingOptions { CitationGateEnabled = gateEnabled }));

    private static async Task<List<ChatEvent>> Drain(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events) list.Add(e);
        return list;
    }

    private static string Answer(IEnumerable<ChatEvent> events) =>
        string.Concat(events.OfType<TokenEvent>().Select(t => t.Text));

    [Fact] // Czysta odpowiedz od razu => ZERO regeneracji (bramka nie moze kosztowac tam, gdzie nie trzeba).
    public async Task Clean_answer_passes_without_regeneration()
    {
        var llm = new SequenceLlm("Odpowiadasz na podstawie art. 415 [1].");
        var events = await Drain(Service(llm).AskAsync("czy ponoszę odpowiedzialność?", [], null, default));

        Assert.Single(llm.Requests);
        Assert.DoesNotContain(events, e => e is RegeneratingEvent);
        var done = Assert.IsType<DoneEvent>(events[^1]);
        Assert.False(done.Regenerated);
        Assert.False(done.Abstained);
    }

    [Fact] // Zmyslony artykul => JEDNA regeneracja; wychodzi druga, czysta wersja.
    public async Task Fabricated_article_triggers_one_regeneration()
    {
        var llm = new SequenceLlm(
            "Zgodnie z art. 999 [1] odpowiadasz.",        // brudna: art. 999 nie ma w kontekście
            "Zgodnie z art. 415 [1] odpowiadasz.");        // czysta

        var events = await Drain(Service(llm).AskAsync("pytanie", [], null, default));

        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains(events, e => e is RegeneratingEvent);
        var done = Assert.IsType<DoneEvent>(events[^1]);
        Assert.True(done.Regenerated);
        Assert.False(done.Abstained);
        Assert.True(done.Check!.IsClean);
    }

    [Fact] // Instrukcja korygujaca wymienia KONKRETNE odwolanie - ogolne "nie zmyslaj" model juz ma
           // w regule 4 promptu i widocznie nie wystarczylo.
    public async Task Correction_names_the_offending_reference()
    {
        var llm = new SequenceLlm("Zgodnie z art. 999 [1].", "Zgodnie z art. 415 [1].");
        await Drain(Service(llm).AskAsync("pytanie", [], null, default));

        var correction = llm.Requests[1].Messages[^1].Content;
        Assert.Contains("999", correction);
        Assert.Contains("KOREKTA", correction);
    }

    [Fact] // Druga proba TEZ brudna => ODMOWA. Halucynowane odwolanie nie wychodzi, koniec.
    public async Task Still_dirty_after_retry_refuses()
    {
        var llm = new SequenceLlm("Zgodnie z art. 999 [1].", "Zgodnie z art. 888 [1].");
        var events = await Drain(Service(llm).AskAsync("pytanie", [], null, default));

        Assert.Equal(2, llm.Requests.Count);                       // dokładnie dwie próby, nie więcej
        var abstain = Assert.IsType<AbstainEvent>(events.Last(e => e is AbstainEvent));
        Assert.Equal(AnswerGate.RefusalMessage, abstain.Message);
        var done = Assert.IsType<DoneEvent>(events[^1]);
        Assert.True(done.Abstained);
    }

    [Fact] // Zmyslona SYGNATURA tez zawraca (sygnal wysokoprecyzyjny).
    public async Task Fabricated_case_number_triggers_regeneration()
    {
        var llm = new SequenceLlm("Jak w wyroku I ACa 123/45 [1].", "Zgodnie z art. 415 [1].");
        var events = await Drain(Service(llm).AskAsync("pytanie", [], null, default));

        Assert.Contains(events, e => e is RegeneratingEvent);
        Assert.Equal(2, llm.Requests.Count);
    }

    [Fact] // Cytat [n] spoza zakresu tez jest naprawialny regeneracja (blad formalny, nie fabrykacja).
    public async Task Out_of_range_citation_triggers_regeneration()
    {
        var llm = new SequenceLlm("Zgodnie z art. 415 [7].", "Zgodnie z art. 415 [1].");
        var events = await Drain(Service(llm).AskAsync("pytanie", [], null, default));

        Assert.Contains(events, e => e is RegeneratingEvent);
        var done = Assert.IsType<DoneEvent>(events[^1]);
        Assert.True(done.Check!.IsClean);
    }

    [Fact] // FLAGA OFF = dzisiejsze zachowanie: brudna odpowiedz wychodzi, badge ja oznacza.
    public async Task Flag_off_keeps_today_behaviour()
    {
        var llm = new SequenceLlm("Zgodnie z art. 999 [1].");
        var events = await Drain(Service(llm, gateEnabled: false)
            .AskAsync("pytanie", [], null, default));

        Assert.Single(llm.Requests);
        Assert.DoesNotContain(events, e => e is RegeneratingEvent);
        Assert.DoesNotContain(events, e => e is AbstainEvent);
        Assert.Contains("999", Answer(events));                    // wychodzi jak dotąd
        var done = Assert.IsType<DoneEvent>(events[^1]);
        Assert.False(done.Check!.IsClean);                          // tylko oznaczona
        Assert.False(done.Regenerated);
    }

    // --- Kontrola fałszywych alarmów: warianty zapisu, które w aktach są normą ---

    [Theory] // Te odpowiedzi sa POPRAWNE - bramka NIE MOZE ich zawracac (inaczej zamieniamy
             // halucynacje na odmowy, czyli porazka).
    [InlineData("Zgodnie z art. 415 ust. 1 [1] odpowiadasz.")]
    [InlineData("Zgodnie z art. 415 § 1 [1] odpowiadasz.")]
    [InlineData("Zgodnie z art. 415 pkt 2 [1] odpowiadasz.")]
    public async Task Article_unit_variants_do_not_trigger_regeneration(string answer)
    {
        var llm = new SequenceLlm(answer);
        var events = await Drain(Service(llm).AskAsync("pytanie", [], null, default));

        Assert.Single(llm.Requests);
        Assert.DoesNotContain(events, e => e is RegeneratingEvent);
        Assert.DoesNotContain(events, e => e is AbstainEvent);
    }

    // --- Czysta funkcja bramki (bez ChatService) ---

    [Fact] // Budzet naprawczy: gdy juz regenerowano, kolejny brud => ODMOWA, nie druga regeneracja.
           // Bez tego mechanizmy naprawcze (Zadania 10/12/13) skumulowalyby sie i tura puchlaby.
    public void Budget_already_spent_means_refuse_not_second_retry()
    {
        var dirty = new CitationCheck([1], [], ["art. 999"], null, null, ["art. 999"], []);

        Assert.Equal(AnswerVerdict.Regenerate, AnswerGate.Decide(dirty, alreadyRegenerated: false).Verdict);
        Assert.Equal(AnswerVerdict.Refuse, AnswerGate.Decide(dirty, alreadyRegenerated: true).Verdict);
    }

    [Fact] // Czysty wynik przechodzi niezaleznie od zuzytego budzetu.
    public void Clean_check_always_passes()
    {
        var clean = new CitationCheck([1], [], [], null, null, [], []);

        Assert.Equal(AnswerVerdict.Pass, AnswerGate.Decide(clean, alreadyRegenerated: false).Verdict);
        Assert.Equal(AnswerVerdict.Pass, AnswerGate.Decide(clean, alreadyRegenerated: true).Verdict);
    }
}
