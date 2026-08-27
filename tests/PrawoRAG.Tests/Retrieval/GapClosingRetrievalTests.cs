using PrawoRAG.Domain;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Tests.Fakes;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// T-GAPCLOSE (Zadanie 12 planu ROU) — druga runda retrievalu zamiast odmowy.
///
/// Dwie własności, które te testy chronią, bo od nich zależy, czy mechanizm jest w ogóle
/// akceptowalny:
/// (1) MOŻE TYLKO DODAĆ kontekst — bramka i walidator działają na końcu bez zmian, więc nie ma
///     nowego trybu halucynacji;
/// (2) KOSZT PŁACĄ TYLKO pytania, które dziś nie dają nic — udana runda 1 nie wywołuje
///     reformulatora ani drugiego retrievalu, czyli nie zwalnia odpowiedzi, które już działają.
/// </summary>
public class GapClosingRetrievalTests
{
    private const double Threshold = 0.55;

    private static RetrievedChunk Chunk(string text) => new()
    {
        ChunkId = Guid.CreateVersion7(), DocumentId = Guid.CreateVersion7(), Text = text,
        Source = "ELI", DocType = DocTypes.Act, Title = "Ustawa", Score = 1.0,
    };

    /// <summary>Reformulator z licznikiem — pozwala dowieść, że NIE jest wołany bez potrzeby.</summary>
    private sealed class CountingReformulator(string? result) : IQueryReformulator
    {
        public int Calls { get; private set; }
        public string? LastQuestion { get; private set; }
        public IReadOnlyList<ChatTurn>? LastHistory { get; private set; }

        public Task<string?> ReformulateAsync(string question, IReadOnlyList<ChatTurn> history, CancellationToken ct)
        {
            Calls++;
            LastQuestion = question;
            LastHistory = history;
            return Task.FromResult(result);
        }
    }

    private static Task<GapClosingRetrieval.Outcome> Run(
        IRetriever retriever, IQueryReformulator? reformulator, int maxExtraRounds = 1,
        string question = "czy pracodawca może mnie zwolnić?", IReadOnlyList<ChatTurn>? history = null) =>
        GapClosingRetrieval.RetrieveAsync(
            retriever, text => new RetrievalQuery { Text = text, TopK = 8 }, question, history ?? [],
            cosineMargin: 0.05, rerankMargin: 0.05, abstentionThreshold: Threshold,
            reformulator, maxExtraRounds, default);

    [Fact] // Runda 1 z pokryciem => ZERO wywolan reformulatora i JEDEN retrieval.
           // To jest gwarancja, ze mechanizm nie zwalnia odpowiedzi, ktore dzis dzialaja.
    public async Task Good_first_round_costs_nothing_extra()
    {
        var retriever = new FakeRetriever(_ => new RetrievalResult([Chunk("art. 415")], 0.9));
        var reformulator = new CountingReformulator("inne zapytanie");

        var outcome = await Run(retriever, reformulator);

        Assert.Equal(0, reformulator.Calls);
        Assert.Single(retriever.Queries);
        Assert.False(outcome.ExtraRound);
        Assert.Null(outcome.ReformulatedQuery);
    }

    [Fact] // Runda 1 bez pokrycia + udane przeformulowanie => DRUGA runda, wyniki scalone.
    public async Task Abstain_triggers_second_round_and_merges()
    {
        var call = 0;
        var retriever = new FakeRetriever(_ => ++call == 1
            ? new RetrievalResult([Chunk("nietrafiony fragment")], 0.20)   // < próg → odmowa
            : new RetrievalResult([Chunk("Prezes UODO — zgłoszenie")], 0.80));
        var reformulator = new CountingReformulator("zgłoszenie naruszenia Prezesowi UODO");

        var outcome = await Run(retriever, reformulator);

        Assert.Equal(1, reformulator.Calls);
        Assert.Equal(2, retriever.Queries.Count);
        Assert.True(outcome.ExtraRound);
        Assert.Equal("zgłoszenie naruszenia Prezesowi UODO", outcome.ReformulatedQuery);
        Assert.Equal(2, outcome.Result.Chunks.Count);                       // obie rundy w kontekście
        Assert.Equal(0.80, outcome.Result.MaxSimilarity);                   // sygnał = maksimum z rund
        Assert.False(AbstentionPolicy.ShouldAbstain(outcome.Result, Threshold)); // odmowa zamieniona
    }

    [Fact] // Fragment z LEPSZEJ rundy jest pierwszy - to on ma najwieksza szanse zasilic odpowiedz.
    public async Task Better_round_chunks_come_first()
    {
        var call = 0;
        var retriever = new FakeRetriever(_ => ++call == 1
            ? new RetrievalResult([Chunk("słaby")], 0.20)
            : new RetrievalResult([Chunk("mocny")], 0.90));

        var outcome = await Run(retriever, new CountingReformulator("inne"));

        Assert.Equal("mocny", outcome.Result.Chunks[0].Text);
    }

    [Fact] // Reformulator zwrocil null (awaria/BRAK/rownowazne wejsciu) => dzisiejsza odmowa,
           // BEZ drugiego retrievalu (nie ma sensu powtarzac tego samego zapytania).
    public async Task Null_reformulation_keeps_today_behaviour()
    {
        var retriever = new FakeRetriever(_ => new RetrievalResult([Chunk("x")], 0.20));
        var reformulator = new CountingReformulator(null);

        var outcome = await Run(retriever, reformulator);

        Assert.Equal(1, reformulator.Calls);
        Assert.Single(retriever.Queries);
        Assert.False(outcome.ExtraRound);
        Assert.True(AbstentionPolicy.ShouldAbstain(outcome.Result, Threshold));
    }

    [Fact] // Druga runda TEZ bez pokrycia => odmowa, ale DOKLADNIE dwie rundy. Nie trzy, nie petla.
    public async Task Second_round_without_coverage_still_refuses_after_exactly_two_rounds()
    {
        var retriever = new FakeRetriever(_ => new RetrievalResult([Chunk("x")], 0.20));

        var outcome = await Run(retriever, new CountingReformulator("inne zapytanie"));

        Assert.Equal(2, retriever.Queries.Count);
        Assert.True(AbstentionPolicy.ShouldAbstain(outcome.Result, Threshold));
    }

    [Fact] // MaxExtraRounds=0 => zachowanie jak przed Faza 4 (wylacznik bez zmiany kodu).
    public async Task Zero_extra_rounds_disables_mechanism()
    {
        var retriever = new FakeRetriever(_ => new RetrievalResult([Chunk("x")], 0.20));
        var reformulator = new CountingReformulator("inne");

        var outcome = await Run(retriever, reformulator, maxExtraRounds: 0);

        Assert.Equal(0, reformulator.Calls);
        Assert.Single(retriever.Queries);
        Assert.False(outcome.ExtraRound);
    }

    [Fact] // Brak reformulatora (eval bez modelu pomocniczego) => tylko runda 1, bez wyjatku.
    public async Task Missing_reformulator_disables_mechanism()
    {
        var retriever = new FakeRetriever(_ => new RetrievalResult([Chunk("x")], 0.20));

        var outcome = await Run(retriever, reformulator: null);

        Assert.Single(retriever.Queries);
        Assert.False(outcome.ExtraRound);
    }

    [Fact] // Dedup po ChunkId: ten sam fragment z obu rund zajmuje JEDEN slot, nie dwa.
    public async Task Deduplicates_chunks_across_rounds()
    {
        var shared = Chunk("wspólny fragment");
        var call = 0;
        var retriever = new FakeRetriever(_ => ++call == 1
            ? new RetrievalResult([shared], 0.20)
            : new RetrievalResult([shared, Chunk("nowy")], 0.80));

        var outcome = await Run(retriever, new CountingReformulator("inne"));

        Assert.Equal(2, outcome.Result.Chunks.Count);
        Assert.Single(outcome.Result.Chunks, c => c.ChunkId == shared.ChunkId);
    }

    [Fact] // Scalenie przyciete do TopK - inaczej prompt puchnie do dwukrotnosci kalibrowanego kontekstu.
    public async Task Merge_is_capped_at_top_k()
    {
        var call = 0;
        var retriever = new FakeRetriever(_ => ++call == 1
            ? new RetrievalResult([Chunk("a1"), Chunk("a2"), Chunk("a3")], 0.20)
            : new RetrievalResult([Chunk("b1"), Chunk("b2"), Chunk("b3")], 0.80));

        var outcome = await GapClosingRetrieval.RetrieveAsync(
            retriever, text => new RetrievalQuery { Text = text, TopK = 4 }, "pytanie", [],
            cosineMargin: 0.05, rerankMargin: 0.05, abstentionThreshold: Threshold,
            new CountingReformulator("inne"), maxExtraRounds: 1, default);

        Assert.Equal(4, outcome.Result.Chunks.Count);
    }

    [Fact] // ExactMatchHits sumowane - trafienie dokladne z KTOREJKOLWIEK rundy przepuszcza bramke.
    public async Task Exact_match_hits_are_summed()
    {
        var call = 0;
        var retriever = new FakeRetriever(_ => ++call == 1
            ? new RetrievalResult([Chunk("x")], 0.20, null, ExactMatchHits: 0)
            : new RetrievalResult([Chunk("y")], 0.20, null, ExactMatchHits: 2));

        var outcome = await Run(retriever, new CountingReformulator("inne"));

        Assert.Equal(2, outcome.Result.ExactMatchHits);
        Assert.False(AbstentionPolicy.ShouldAbstain(outcome.Result, Threshold)); // trafienie dokładne
    }

    [Fact] // Reformulator dostaje ORYGINALNE pytanie uzytkownika, nie sklejke z historia.
    public async Task Reformulator_receives_original_question()
    {
        var retriever = new FakeRetriever(_ => new RetrievalResult([Chunk("x")], 0.20));
        var reformulator = new CountingReformulator("inne");

        await Run(retriever, reformulator, question: "komu zgłosić wyciek danych?");

        Assert.Equal("komu zgłosić wyciek danych?", reformulator.LastQuestion);
    }

    // --- HISTORIA DLA REFORMULATORA (fix 2026-08-27) ---
    // Defekt: reformulator dostawał goły string, więc na follow-upie przekładał na terminologię
    // ustawową samo „a co z § 2?" — tekst bez tematu. Właśnie w tej klasie tur odmowy są
    // najczęstsze, czyli tam, gdzie druga runda ma najwięcej do uratowania.

    [Fact]
    public async Task Reformulator_receives_history()
    {
        var retriever = new FakeRetriever(_ => new RetrievalResult([Chunk("x")], 0.20));
        var reformulator = new CountingReformulator("art. 367 § 2 KPC solidarność dłużników");
        ChatTurn[] history = [new("co mówi art. 367 KPC?", "Art. 367 KPC dotyczy solidarności dłużników.")];

        await Run(retriever, reformulator, question: "a co z § 2?", history: history);

        Assert.Equal("a co z § 2?", reformulator.LastQuestion);          // pytanie surowe, jak dotąd
        var passed = Assert.Single(reformulator.LastHistory!);           // ale historia DOCHODZI
        Assert.Equal("co mówi art. 367 KPC?", passed.Question);
    }

    [Fact] // Runda 2 to JEDEN retrieval, nie dwa - przeformulowane zapytanie jest juz samodzielne
           // (reformulator widzial rozmowe), wiec sklejanie go z historia tylko rozmylo by embedding
           // i podwoilo koszt rundy.
    public async Task Second_round_does_not_re_glue_history()
    {
        var call = 0;
        var retriever = new FakeRetriever(_ => ++call <= 2
            ? new RetrievalResult([Chunk("nietrafione")], 0.20)         // runda 1: surowe + kontekstowe
            : new RetrievalResult([Chunk("trafione")], 0.80));
        var reformulator = new CountingReformulator("solidarność dłużników art. 367 § 2 KPC");
        ChatTurn[] history = [new("co mówi art. 367 KPC?", "Solidarność dłużników.")];

        var outcome = await Run(retriever, reformulator, question: "a co z § 2?", history: history);

        // 2 (runda 1: surowe + sklejka, bo pytanie użytkownika NIE jest samodzielne) + 1 (runda 2).
        Assert.Equal(3, retriever.Queries.Count);
        Assert.Equal("solidarność dłużników art. 367 § 2 KPC", retriever.Queries[^1].Text);
        Assert.True(outcome.ExtraRound);
    }
}
