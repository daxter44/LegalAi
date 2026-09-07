using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrawoRAG.Api.Services.Plans;
using PrawoRAG.Storage;

namespace PrawoRAG.Tests.Access;

/// <summary>
/// Magazyn liczników na ŻYWYM Postgresie (E1/T-10). Tylko baza dowodzi trzech rzeczy: trwałości po
/// restarcie procesu, atomowości przy równoległych zapytaniach i tego, że nowy okres startuje od zera
/// bez żadnego zadania czyszczącego. Reguły dwóch osi są w CostGuardRulesTests (bez bazy).
/// </summary>
[Collection("LiveDb")]
public class CostGuardLiveTests : IAsyncLifetime
{
    private static readonly string Conn =
        Environment.GetEnvironmentVariable("PRAWORAG_DB")
        ?? "Host=localhost;Port=5432;Database=praworag;Username=praworag;Password=praworag";

    private const string Key = "test-licznik-uzycia";
    private const string Scope = "test_scope";
    private readonly ServiceProvider _provider;
    private readonly PostgresUsageCounters _counters;

    public CostGuardLiveTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<PrawoRagDbContext>(o => o.UseNpgsql(Conn, x => x.UseVector()));
        _provider = services.BuildServiceProvider();
        _counters = new PostgresUsageCounters(_provider.GetRequiredService<IServiceScopeFactory>());
    }

    /// <summary>Sprząta WYŁĄCZNIE wiersze tego testu — baza wspólna z korpusem.</summary>
    public async Task InitializeAsync() => await CleanAsync();

    public async Task DisposeAsync()
    {
        await CleanAsync();
        await _provider.DisposeAsync();
    }

    private async Task CleanAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();
        await db.UsageCounters.Where(c => c.Key == Key).ExecuteDeleteAsync();
    }

    private static DateOnly Period(int y, int m, int d) => new(y, m, d);

    [Fact]
    public async Task Conditional_increment_stops_exactly_at_the_limit()
    {
        var period = Period(2026, 8, 1);

        for (var i = 1; i <= 3; i++)
            Assert.Equal(i, await _counters.TryIncrementAsync(Scope, Key, period, 3));

        Assert.Null(await _counters.TryIncrementAsync(Scope, Key, period, 3));
    }

    [Fact]
    public async Task Counter_survives_a_new_process()
    {
        var period = Period(2026, 8, 2);
        await _counters.TryIncrementAsync(Scope, Key, period, 5);

        // Nowa instancja z własnym scope factory = to, co dzieje się przy restarcie aplikacji.
        // Wcześniej licznik żył w pamięci procesu i restart kasował wykorzystany limit.
        var afterRestart = new PostgresUsageCounters(_provider.GetRequiredService<IServiceScopeFactory>());

        Assert.Equal(1, await afterRestart.CurrentAsync(Scope, Key, period));
        Assert.Equal(2, await afterRestart.TryIncrementAsync(Scope, Key, period, 5));
    }

    [Fact]
    public async Task Parallel_increments_never_exceed_the_limit()
    {
        // Sedno T-10: „odczytaj, sprawdź, zapisz" przepuszcza nadmiar, gdy użytkownik ma otwarte dwie
        // karty. Decyzja musi zapadać w jednym zapytaniu do bazy.
        // Współbieżność realistyczna dla jednego konta (kilka kart/zakładek), nie obciążeniowa —
        // każde równoległe wywołanie trzyma własne połączenie i czeka na blokadę TEGO SAMEGO wiersza,
        // więc kilkadziesiąt naraz testowałoby pulę połączeń, nie regułę limitu.
        var period = Period(2026, 8, 3);
        const int limit = 3;
        const int attempts = 8;

        var results = await Task.WhenAll(Enumerable.Range(0, attempts)
            .Select(_ => _counters.TryIncrementAsync(Scope, Key, period, limit)));

        Assert.Equal(limit, results.Count(r => r is not null));
        Assert.Equal(limit, await _counters.CurrentAsync(Scope, Key, period));
    }

    [Fact]
    public async Task Different_period_is_a_different_row_so_it_starts_from_zero()
    {
        await _counters.TryIncrementAsync(Scope, Key, Period(2026, 8, 4), 1);

        Assert.Null(await _counters.TryIncrementAsync(Scope, Key, Period(2026, 8, 4), 1));
        Assert.Equal(1, await _counters.TryIncrementAsync(Scope, Key, Period(2026, 9, 4), 1));
    }

    [Fact]
    public async Task Refund_never_goes_below_zero()
    {
        var period = Period(2026, 8, 5);

        await _counters.AddAsync(Scope, Key, period, -5); // zwrot bez wcześniejszej rezerwacji

        Assert.Equal(0, await _counters.CurrentAsync(Scope, Key, period));
    }
}
