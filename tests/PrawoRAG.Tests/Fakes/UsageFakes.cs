using System.Collections.Concurrent;
using PrawoRAG.Api.Services.Plans;

namespace PrawoRAG.Tests.Fakes;

/// <summary>
/// Liczniki zużycia w pamięci — ten sam kontrakt co postgresowe, w tym atomowość (tu przez
/// <see cref="ConcurrentDictionary{TKey,TValue}.AddOrUpdate(TKey,Func{TKey,TValue},Func{TKey,TValue,TValue})"/>).
/// Pozwala testować REGUŁY bramki kosztów bez bazy; sam zapis dowodzi CostGuardLiveTests.
/// </summary>
public sealed class MemoryUsageCounters : IUsageCounters
{
    private readonly ConcurrentDictionary<(string Scope, string Key, DateOnly Period), long> _values = new();

    public Task<long?> TryIncrementAsync(string scope, string key, DateOnly period, long limit, CancellationToken ct = default)
    {
        if (limit <= 0) return Task.FromResult<long?>(null);

        long? result = null;
        _values.AddOrUpdate((scope, key, period),
            _ => { result = 1; return 1; },
            (_, current) =>
            {
                if (current >= limit) { result = null; return current; }
                result = current + 1;
                return current + 1;
            });
        return Task.FromResult(result);
    }

    public Task AddAsync(string scope, string key, DateOnly period, long delta, CancellationToken ct = default)
    {
        _values.AddOrUpdate((scope, key, period), _ => Math.Max(delta, 0), (_, v) => Math.Max(v + delta, 0));
        return Task.CompletedTask;
    }

    public Task<long> CurrentAsync(string scope, string key, DateOnly period, CancellationToken ct = default) =>
        Task.FromResult(_values.GetValueOrDefault((scope, key, period)));
}

/// <summary>Uprawnienie podstawiane wprost — testy bramki nie muszą zakładać konta w bazie.</summary>
public sealed class FixedEntitlements(PlanLimits? limits, BillingPeriod period) : IEntitlements
{
    public Task<Entitlement> ForAsync(string userId, CancellationToken ct = default) =>
        Task.FromResult(new Entitlement(userId, limits is null ? "brak" : "test", limits, period));
}

/// <summary>Tryb sprzed kont: plan nie obowiązuje, zostają wyłącznie capy pojemności.</summary>
public sealed class NoPlanEntitlements : IEntitlements
{
    private static readonly BillingPeriod Period = new(
        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

    public Task<Entitlement> ForAsync(string userId, CancellationToken ct = default) =>
        Task.FromResult(new Entitlement(userId, "brak", null, Period));
}
