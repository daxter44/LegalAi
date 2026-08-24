using Microsoft.EntityFrameworkCore;
using Pgvector;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Storage;
using PrawoRAG.Storage.Entities;
using PrawoRAG.Storage.Retrieval;
using PrawoRAG.Tests.Fakes;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// T-STAGE (Zadanie 2 planu ROU) — raportowanie etapów retrievalu na żywo. Powód: pytanie prawne
/// trwa ~85 s (pomiar PRAWORAG_LOG_TIMING), a UI nie miało czym pokazać, że system pracuje.
/// Etapy są raportowane w TYCH SAMYCH punktach, gdzie stoi <see cref="LatencyLog"/> — dlatego testy
/// pilnują nazw etapów (kontrakt z instrumentacją) oraz tego, że brak słuchacza nie zmienia wyniku.
/// </summary>
[Collection("LiveDb")]
public class RetrievalStageTests
{
    private static readonly string Conn =
        Environment.GetEnvironmentVariable("PRAWORAG_DB")
        ?? "Host=localhost;Port=5432;Database=praworag;Username=praworag;Password=praworag";

    private static PrawoRagDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PrawoRagDbContext>().UseNpgsql(Conn, o => o.UseVector()).Options);

    private static readonly FakeEmbeddingProvider Emb = new();

    private static async Task CleanAsync(string source)
    {
        await using var db = NewDb();
        await db.Documents.Where(d => d.Source == source).ExecuteDeleteAsync();
    }

    private static async Task SeedAsync(string source, string text)
    {
        var vec = (await Emb.EmbedPassagesAsync([text], default))[0];
        await using var db = NewDb();
        var doc = new DocumentEntity
        {
            Id = Guid.CreateVersion7(), Source = source, ExternalId = "s1", DocType = DocTypes.Act,
            Title = $"{source}/s1", ContentHash = $"{source}:s1", Status = DocumentStatus.Indexed,
            InForce = true, IngestedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(doc);
        db.Chunks.Add(new ChunkEntity
        {
            Id = Guid.CreateVersion7(), DocumentId = doc.Id, ChunkIndex = 0, Text = text,
            TokenCount = 20, CharStart = 0, CharEnd = text.Length,
            Embedding = new Vector(vec), EmbeddedWith = Emb.ModelId,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Zbiera etapy do listy — tak jak zrobi to kanał zdarzeń w ChatService (Zadanie 3).</summary>
    private sealed class Collector : IProgress<RetrievalStage>
    {
        public List<RetrievalStage> Stages { get; } = [];
        public void Report(RetrievalStage value) => Stages.Add(value);
    }

    private static async Task<RetrievalResult> RetrieveAsync(RetrievalQuery query)
    {
        await using var db = NewDb();
        return await new HybridRetriever(db, Emb).RetrieveAsync(query, default);
    }

    [Fact] // Etapy leca w porzadku pipeline'u i uzywaja nazw ZGODNYCH z LatencyLog (jedno zrodlo prawdy).
    public async Task Reports_stages_in_pipeline_order()
    {
        const string src = "TEST-STAGE-1";
        await CleanAsync(src);
        const string text = "Stagetako unikalny przepis testowy alfa beta gamma delta epsilon";
        await SeedAsync(src, text);

        var collector = new Collector();
        await RetrieveAsync(new RetrievalQuery { Text = text, MinChunkTokens = 0, Progress = collector });

        var names = collector.Stages.Select(s => s.Name).ToList();
        Assert.Equal("embed", names[0]);            // pierwszy etap ZAWSZE embedding pytania
        Assert.Contains("dense", names);
        Assert.Contains("sparse", names);
        Assert.Contains("fetch_candidates", names);
        // Tory dokładne lecą PO fuzji i pobraniu kandydatów — kolejność jest kontraktem dla UI.
        Assert.True(names.IndexOf("fetch_candidates") < names.IndexOf("lane.signature"));
        Assert.True(names.IndexOf("dense") < names.IndexOf("fetch_candidates"));
        // Każdy etap ma etykietę dla użytkownika — pusty label to błąd (UI pokazałoby puste miejsce).
        Assert.All(collector.Stages, s => Assert.False(string.IsNullOrWhiteSpace(s.Label)));

        await CleanAsync(src);
    }

    [Fact] // Liczby przy etapach (buduja zaufanie do czekania): dense/sparse znaja pule kandydatow.
    public async Task Reports_candidate_counts_where_known()
    {
        const string src = "TEST-STAGE-2";
        await CleanAsync(src);
        const string text = "Countako unikalny przepis testowy zeta eta theta iota kappa";
        await SeedAsync(src, text);

        var collector = new Collector();
        await RetrieveAsync(new RetrievalQuery
        {
            Text = text, MinChunkTokens = 0, CandidatesPerPath = 37, Progress = collector,
        });

        Assert.Equal(37, collector.Stages.Single(s => s.Name == "dense").Count);
        Assert.Equal(37, collector.Stages.Single(s => s.Name == "sparse").Count);
        await CleanAsync(src);
    }

    [Fact] // Rownowaznosc: brak sluchacza => IDENTYCZNY wynik retrievalu (Eval, /api/search, testy).
    public async Task Without_progress_result_is_identical()
    {
        const string src = "TEST-STAGE-3";
        await CleanAsync(src);
        const string text = "Equivtako unikalny przepis testowy lambda mu ni ksi omikron";
        await SeedAsync(src, text);

        var query = new RetrievalQuery { Text = text, MinChunkTokens = 0 };
        var withoutProgress = await RetrieveAsync(query);
        var withProgress = await RetrieveAsync(query with { Progress = new Collector() });

        Assert.Equal(withoutProgress.Chunks.Count, withProgress.Chunks.Count);
        Assert.Equal(
            withoutProgress.Chunks.Select(c => c.ChunkId),
            withProgress.Chunks.Select(c => c.ChunkId));
        Assert.Equal(withoutProgress.MaxSimilarity, withProgress.MaxSimilarity);
        Assert.Equal(withoutProgress.ExactMatchHits, withProgress.ExactMatchHits);
        await CleanAsync(src);
    }

    [Fact] // Follow-up = DWA pelne przebiegi; bez prefiksu UI pokazaloby te same etapy dwukrotnie
           // bez wyjasnienia, dlaczego odpowiedz trwa dwa razy dluzej.
    public async Task Follow_up_labels_both_retrieval_passes()
    {
        var retriever = new FakeRetriever(_ => new RetrievalResult([], 0.9));

        await FollowUpSelector.SelectAsync(
            retriever,
            text => new RetrievalQuery { Text = text },
            "a co z § 2?",
            [new ChatTurn("pytanie 1", "odpowiedź 1")],
            cosineMargin: 0.05, rerankMargin: 0.05, ct: default);

        Assert.Equal(2, retriever.Queries.Count); // źródło podwójnego czasu odpowiedzi
        Assert.Equal("(1/2) ", retriever.Queries[0].ProgressLabelPrefix);
        Assert.Equal("(2/2) ", retriever.Queries[1].ProgressLabelPrefix);
    }

    [Fact] // Prefiks realnie trafia do etykiety etapu (kontrakt ReportStage), a nie tylko do zapytania.
    public void Prefix_is_applied_to_stage_label()
    {
        var collector = new Collector();
        var query = new RetrievalQuery
        {
            Text = "t", Progress = collector, ProgressLabelPrefix = "(2/2) ",
        };

        query.ReportStage("rerank.main", "Oceniam trafność kandydatów…", 50);

        var stage = collector.Stages.Single();
        Assert.Equal("rerank.main", stage.Name);                       // nazwa techniczna BEZ prefiksu
        Assert.Equal("(2/2) Oceniam trafność kandydatów…", stage.Label); // etykieta Z prefiksem
        Assert.Equal(50, stage.Count);
    }

    [Fact] // Bez historii NIE ma prefiksu - "(1/2)" przy jednym przebiegu byloby klamstwem.
    public async Task Single_pass_has_no_prefix()
    {
        var retriever = new FakeRetriever(_ => new RetrievalResult([], 0.9));

        await FollowUpSelector.SelectAsync(
            retriever, text => new RetrievalQuery { Text = text }, "pytanie", [],
            cosineMargin: 0.05, rerankMargin: 0.05, ct: default);

        Assert.Single(retriever.Queries);
        Assert.Null(retriever.Queries[0].ProgressLabelPrefix);
    }

    [Fact] // Brak sluchacza => ReportStage jest no-opem (zero kosztu, zero wyjatkow).
    public void Report_stage_without_listener_is_noop()
    {
        var query = new RetrievalQuery { Text = "t" };
        query.ReportStage("embed", "Zamieniam pytanie na wektor…"); // nie może rzucić
    }
}
