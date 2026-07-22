using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrawoRAG.Api.Services;
using PrawoRAG.Storage;

namespace PrawoRAG.Tests.Analysis;

/// <summary>
/// T-AN-3 — persystencja raportu analizy na ŻYWYM Postgresie: roundtrip bez treści dokumentu,
/// scoping po UserId (cudzy id → null/no-op), upsert po kluczu naturalnym, statusy terminalne
/// (MarkInterrupted nie nadpisuje Done), feedback 1:1.
/// </summary>
[Collection("LiveDb")]
public class AnalysisStoreTests : IAsyncLifetime
{
    private static readonly string Conn =
        Environment.GetEnvironmentVariable("PRAWORAG_DB")
        ?? "Host=localhost;Port=5432;Database=praworag;Username=praworag;Password=praworag";

    private const string User = "test-an3@local";
    private readonly AnalysisStore _store;
    private readonly ServiceProvider _provider;

    public AnalysisStoreTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<PrawoRagDbContext>(o => o.UseNpgsql(Conn, x => x.UseVector()));
        _provider = services.BuildServiceProvider();
        _store = new AnalysisStore(_provider.GetRequiredService<IServiceScopeFactory>());
    }

    /// <summary>Sprząta WYŁĄCZNIE dane tego testu (po UserId) — baza wspólna z korpusem.</summary>
    public async Task InitializeAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();
        await db.Analyses.Where(a => a.UserId == User || a.UserId == "obcy@local").ExecuteDeleteAsync();
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private static UnitAnalysis Unit(int index, UnitVerdict verdict = UnitVerdict.Ok, string? error = null) =>
        new(index, $"§ {index}", verdict, verdict == UnitVerdict.Error ? null : $"Odpowiedź {index} [1].",
            [new ChatSource(1, "art. 484 KC", "Kodeks cywilny", "https://x", "snippet…")], Error: error);

    private async Task<Guid> CreateAsync(string user = User, int total = 3)
    {
        var id = Guid.CreateVersion7();
        await _store.CreateAsync(id, user, "umowa.pdf", 5, "oceń ryzyka", total, unitsTruncated: false, default);
        return id;
    }

    [Fact]
    public async Task Roundtrip_without_document_text()
    {
        var id = await CreateAsync();
        await _store.UpsertUnitAsync(id, Unit(1), default);
        await _store.UpsertUnitAsync(id, Unit(2, UnitVerdict.Risk), default);
        await _store.CompleteAsync(id, "Streszczenie.", default);

        var a = await _store.GetAsync(id, User, default);

        Assert.NotNull(a);
        Assert.Equal(AnalysisStatus.Done, a.Status);
        Assert.Equal("Streszczenie.", a.Summary);
        Assert.Equal(3, a.UnitsTotal);
        Assert.Equal(2, a.Units.Count);                      // jednostka 3 nie dotarła — częściowość jest naturalna
        Assert.Equal([1, 2], a.Units.Select(u => u.UnitIndex));
        Assert.Equal(UnitVerdict.Risk, a.Units[1].Verdict);
        var src = Assert.Single(a.Units[0].Sources);         // źródła przetrwały roundtrip jsonb
        Assert.Equal("art. 484 KC", src.Label);
        Assert.Equal("https://x", src.Url);
    }

    [Fact]
    public async Task Foreign_user_gets_null_and_empty_list()
    {
        var id = await CreateAsync();

        Assert.Null(await _store.GetAsync(id, "obcy@local", default));
        Assert.Empty(await _store.ListAsync("obcy@local", 50, default));
        Assert.Single(await _store.ListAsync(User, 50, default));
    }

    [Fact]
    public async Task Upsert_is_idempotent_by_natural_key()
    {
        var id = await CreateAsync();
        await _store.UpsertUnitAsync(id, Unit(1, UnitVerdict.Error, error: "awaria"), default);
        await _store.UpsertUnitAsync(id, Unit(1), default); // retry nadpisuje

        var a = await _store.GetAsync(id, User, default);
        var unit = Assert.Single(a!.Units);
        Assert.Equal(UnitVerdict.Ok, unit.Verdict);
        Assert.Null(unit.Error);
    }

    [Fact]
    public async Task MarkInterrupted_does_not_touch_terminal_states()
    {
        var done = await CreateAsync();
        await _store.CompleteAsync(done, null, default);
        await _store.MarkInterruptedAsync(done, default);
        Assert.Equal(AnalysisStatus.Done, (await _store.GetAsync(done, User, default))!.Status);

        var running = await CreateAsync();
        await _store.MarkInterruptedAsync(running, default);
        Assert.Equal(AnalysisStatus.Interrupted, (await _store.GetAsync(running, User, default))!.Status);
    }

    [Fact]
    public async Task MarkAllInterrupted_sweeps_only_analyzing()
    {
        var running = await CreateAsync();
        var done = await CreateAsync();
        await _store.CompleteAsync(done, null, default);

        var swept = await _store.MarkAllInterruptedAsync(default);

        Assert.True(swept >= 1);
        Assert.Equal(AnalysisStatus.Interrupted, (await _store.GetAsync(running, User, default))!.Status);
        Assert.Equal(AnalysisStatus.Done, (await _store.GetAsync(done, User, default))!.Status);
    }

    [Fact]
    public async Task Unit_feedback_own_once_foreign_ignored()
    {
        var id = await CreateAsync();
        await _store.UpsertUnitAsync(id, Unit(1), default);
        var unitId = (await _store.GetAsync(id, User, default))!.Units[0].Id;

        await _store.AddUnitFeedbackAsync(unitId, "obcy@local", "up", null, default);   // cudzy → no-op
        Assert.Null((await _store.GetAsync(id, User, default))!.Units[0].FeedbackGiven);

        await _store.AddUnitFeedbackAsync(unitId, User, "wrong-answer", "nota", default);
        await _store.AddUnitFeedbackAsync(unitId, User, "up", null, default);           // druga → ignorowana (1:1)
        Assert.Equal("wrong-answer", (await _store.GetAsync(id, User, default))!.Units[0].FeedbackGiven);
    }

    [Fact]
    public async Task Fail_records_error()
    {
        var id = await CreateAsync();
        await _store.FailAsync(id, "awaria providera", default);

        var a = await _store.GetAsync(id, User, default);
        Assert.Equal(AnalysisStatus.Failed, a!.Status);
        Assert.Equal("awaria providera", a.Error);
    }
}
