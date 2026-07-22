using Microsoft.Extensions.Options;
using PrawoRAG.Api.Services;

namespace PrawoRAG.Tests.Analysis;

/// <summary>
/// T-SPK-2 — sesja analizy i jej magazyn: cykl życia statusów, licznik postępu przy współbieżnym
/// zapisie wyników, TTL od ostatniego dostępu (fake zegar), sesja wygasła = nie istnieje.
/// </summary>
public class AnalysisSessionStoreTests
{
    /// <summary>Zegar do testów TTL — przesuwany ręcznie.</summary>
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static IReadOnlyList<DocUnit> Units(int n) =>
        Enumerable.Range(1, n).Select(i => new DocUnit(i, $"§ {i}", $"§ {i} treść jednostki")).ToList();

    private static AnalysisSessionStore Store(FakeTime time, int ttlMinutes = 60) =>
        new(time, Options.Create(new AnalysisOptions { SessionTtlMinutes = ttlMinutes }));

    private static UnitAnalysis Result(int index) =>
        new(index, $"§ {index}", UnitVerdict.Ok, "Wszystko w porządku [1].", []);

    [Fact]
    public void Session_lifecycle_and_progress()
    {
        var session = Store(new FakeTime()).Create("tester", "umowa.pdf", 3, "oceń ryzyka", Units(2), unitsTruncated: false);

        Assert.Equal(AnalysisStatus.Preparing, session.Snapshot().Status);

        session.SetStatus(AnalysisStatus.Analyzing);
        session.SetUnitResult(Result(2));
        var mid = session.Snapshot();
        Assert.Equal(1, mid.Completed);
        Assert.Null(mid.Results[0]);            // kolejność dokumentu, nie ukończenia
        Assert.NotNull(mid.Results[1]);

        session.SetUnitResult(Result(1));
        session.SetStatus(AnalysisStatus.Summarizing);
        session.Complete("Streszczenie raportu.");

        var done = session.Snapshot();
        Assert.Equal(AnalysisStatus.Done, done.Status);
        Assert.Equal(2, done.Completed);
        Assert.Equal("Streszczenie raportu.", done.Summary);
    }

    [Fact]
    public void Changed_fires_on_progress_and_completion()
    {
        var session = Store(new FakeTime()).Create("tester", "u.pdf", 1, "p", Units(1), false);
        var fired = 0;
        session.Changed += () => fired++;

        session.SetStatus(AnalysisStatus.Analyzing);
        session.SetUnitResult(Result(1));
        session.Complete(null);

        Assert.Equal(3, fired);
    }

    [Fact]
    public void Fail_sets_status_and_error()
    {
        var session = Store(new FakeTime()).Create("tester", "u.pdf", 1, "p", Units(1), false);
        session.Fail("awaria LLM");

        var snap = session.Snapshot();
        Assert.Equal(AnalysisStatus.Failed, snap.Status);
        Assert.Equal("awaria LLM", snap.Error);
    }

    [Fact]
    public async Task Concurrent_result_writes_count_once_per_unit()
    {
        var session = Store(new FakeTime()).Create("tester", "u.pdf", 10, "p", Units(50), false);

        await Task.WhenAll(Enumerable.Range(1, 50).Select(i => Task.Run(() =>
        {
            session.SetUnitResult(Result(i));
            session.SetUnitResult(Result(i)); // powtórka (retry) nie dubluje licznika
        })));

        var snap = session.Snapshot();
        Assert.Equal(50, snap.Completed);
        Assert.All(snap.Results, Assert.NotNull);
    }

    [Fact]
    public void Expired_session_is_gone_and_access_refreshes_ttl()
    {
        var time = new FakeTime();
        var store = Store(time, ttlMinutes: 60);
        var session = store.Create("tester", "u.pdf", 1, "p", Units(1), false);

        time.Now = time.Now.AddMinutes(45);
        Assert.NotNull(store.TryGet(session.Id, "tester"));   // dostęp odświeża TTL

        time.Now = time.Now.AddMinutes(45);         // 45 min od OSTATNIEGO dostępu < 60
        Assert.NotNull(store.TryGet(session.Id, "tester"));

        time.Now = time.Now.AddMinutes(61);
        Assert.Null(store.TryGet(session.Id, "tester"));      // wygasła = nie istnieje

        time.Now = time.Now.AddMinutes(-61);        // nawet cofnięcie zegara jej nie wskrzesi
        Assert.Null(store.TryGet(session.Id, "tester"));
    }

    [Fact]
    public void Unknown_id_returns_null()
    {
        Assert.Null(Store(new FakeTime()).TryGet(Guid.NewGuid(), "tester"));
    }

    [Fact] // id sesji widać w UI — sam Guid nie może być biletem do cudzego dokumentu
    public void Foreign_user_cannot_access_session()
    {
        var store = Store(new FakeTime());
        var session = store.Create("tester", "u.pdf", 1, "p", Units(1), false);

        Assert.Null(store.TryGet(session.Id, "intruz"));
        Assert.NotNull(store.TryGet(session.Id, "tester")); // właściciel bez zmian
    }

    [Fact] // wygaśnięcie/usunięcie sesji anuluje jej token — runner w locie dostaje sygnał stopu
    public void Sweep_and_remove_cancel_session_token()
    {
        var time = new FakeTime();
        var store = Store(time, ttlMinutes: 60);

        var removed = store.Create("tester", "a.pdf", 1, "p", Units(1), false);
        store.Remove(removed.Id);
        Assert.True(removed.Token.IsCancellationRequested);

        var expired = store.Create("tester", "b.pdf", 1, "p", Units(1), false);
        time.Now = time.Now.AddMinutes(61);
        Assert.Null(store.TryGet(expired.Id, "tester"));
        Assert.True(expired.Token.IsCancellationRequested);
    }

    [Fact] // analiza w toku sama przedłuża sobie TTL (SetUnitResult odświeża LastTouched)
    public void Unit_result_refreshes_ttl()
    {
        var time = new FakeTime();
        var store = Store(time, ttlMinutes: 60);
        var session = store.Create("tester", "u.pdf", 1, "p", Units(1), false);

        time.Now = time.Now.AddMinutes(59);
        session.SetUnitResult(Result(1)); // runner kończy jednostkę → sesja żyje dalej

        time.Now = time.Now.AddMinutes(59);
        Assert.NotNull(store.TryGet(session.Id, "tester"));
    }

    [Fact] // retry: MarkUnitPending cofa wynik, licznik i status
    public void MarkUnitPending_reverts_unit()
    {
        var session = Store(new FakeTime()).Create("tester", "u.pdf", 1, "p", Units(2), false);
        session.SetUnitResult(new UnitAnalysis(1, "§ 1", UnitVerdict.Error, null, [], Error: "awaria"));
        session.SetUnitResult(Result(2));
        session.Complete(null);

        Assert.Equal([1], session.ErrorUnitIndexes());
        session.MarkUnitPending(1);

        var snap = session.Snapshot();
        Assert.Equal(AnalysisStatus.Analyzing, snap.Status);
        Assert.Equal(1, snap.Completed);
        Assert.Null(snap.Results[0]);
        Assert.NotNull(snap.Results[1]); // jednostka OK nietknięta
    }
}
