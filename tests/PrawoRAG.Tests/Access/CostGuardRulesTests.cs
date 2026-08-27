using Microsoft.Extensions.Options;
using PrawoRAG.Api.Services;
using PrawoRAG.Api.Services.Plans;
using PrawoRAG.Tests.Fakes;

namespace PrawoRAG.Tests.Access;

/// <summary>
/// Reguły bramki kosztów (E1/T-10) — bez bazy. Sprawdzamy to, co decyduje o zachowaniu wobec
/// klienta: rozdział osi (plan vs pojemność), zwrot rezerwacji przy odbiciu przez nasz cap,
/// zachowanie trybu bez kont (alfa bit w bit) i rollover dnia. Atomowość i trwałość zapisu dowodzi
/// CostGuardLiveTests na żywym Postgresie.
/// </summary>
public class CostGuardRulesTests
{
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static BillingPeriod Period(int y, int m, int d) =>
        new(new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc).AddMonths(1));

    private static PlanLimits Plan(int perMonth, string name = "Darmowy") =>
        new() { DisplayName = name, RequestsPerMonth = perMonth };

    private static CostGuard Guard(AccessOptions access, PlanLimits? limits, FakeTime time,
        IUsageCounters? counters = null) =>
        new(counters ?? new MemoryUsageCounters(), Options.Create(access),
            new FixedEntitlements(limits, Period(2026, 7, 1)), time);

    // --- tryb bez kont: zachowanie z alfy ---

    [Fact]
    public async Task Disabled_gate_without_plan_always_allows()
    {
        var guard = Guard(new AccessOptions { Enabled = false, MaxUserRequestsPerDay = 0 }, limits: null, new FakeTime());

        var decision = await guard.TryAcquireAsync("ktokolwiek");

        Assert.True(decision.Allowed);
        Assert.Null(decision.Message);
    }

    [Fact]
    public async Task Without_plan_daily_per_user_limit_still_applies()
    {
        // Bramka invite: tożsamością jest nazwa testera, limit dobowy jak w alfie.
        var guard = Guard(new AccessOptions { Enabled = true, MaxUserRequestsPerDay = 2 }, limits: null, new FakeTime());

        Assert.True((await guard.TryAcquireAsync("jan")).Allowed);
        Assert.True((await guard.TryAcquireAsync("jan")).Allowed);
        var denied = await guard.TryAcquireAsync("jan");
        Assert.False(denied.Allowed);
        Assert.Contains("Twój dzienny limit", denied.Message);
        Assert.True((await guard.TryAcquireAsync("anna")).Allowed); // limit per OSOBA
    }

    [Fact]
    public async Task Day_rollover_resets_daily_counters()
    {
        var time = new FakeTime();
        var guard = Guard(new AccessOptions { Enabled = true, MaxUserRequestsPerDay = 1 }, limits: null, time);

        Assert.True((await guard.TryAcquireAsync("jan")).Allowed);
        Assert.False((await guard.TryAcquireAsync("jan")).Allowed);

        time.Now = time.Now.AddDays(1); // północ UTC minęła

        Assert.True((await guard.TryAcquireAsync("jan")).Allowed);
    }

    // --- oś planu ---

    [Fact]
    public async Task Plan_limit_replaces_daily_limit_when_account_has_a_plan()
    {
        var access = new AccessOptions { Enabled = true, MaxUserRequestsPerDay = 1 };
        var guard = Guard(access, Plan(3), new FakeTime());

        // Limit dobowy z AccessOptions NIE obowiązuje kontu z planem — liczy się plan.
        Assert.True((await guard.TryAcquireAsync("konto")).Allowed);
        Assert.True((await guard.TryAcquireAsync("konto")).Allowed);
        Assert.True((await guard.TryAcquireAsync("konto")).Allowed);

        var denied = await guard.TryAcquireAsync("konto");
        Assert.False(denied.Allowed);
        Assert.Contains("limit planu Darmowy", denied.Message);
    }

    [Fact]
    public async Task Free_plan_message_points_at_the_paid_plan()
    {
        var guard = Guard(new AccessOptions { Enabled = false }, Plan(1), new FakeTime());
        await guard.TryAcquireAsync("konto");

        var denied = await guard.TryAcquireAsync("konto");

        Assert.Contains("Odnowi się", denied.Message);        // kiedy wróci limit
        Assert.Contains("Plan Pro", denied.Message);          // miejsce konwersji, nie sam błąd
    }

    [Fact]
    public async Task Paid_plan_message_does_not_advertise_itself()
    {
        var guard = Guard(new AccessOptions { Enabled = false }, Plan(300, "Pro"), new FakeTime());
        for (var i = 0; i < 300; i++) await guard.TryAcquireAsync("konto");

        var denied = await guard.TryAcquireAsync("konto");

        Assert.False(denied.Allowed);
        Assert.DoesNotContain("Plan Pro daje", denied.Message);
    }

    // --- oś pojemności ---

    [Fact]
    public async Task Global_daily_request_cap_is_hard_regardless_of_plan()
    {
        var access = new AccessOptions { Enabled = true, MaxGlobalRequestsPerDay = 2 };
        var counters = new MemoryUsageCounters();
        var jan = Guard(access, Plan(100), new FakeTime(), counters);
        var anna = Guard(access, Plan(100), new FakeTime(), counters);

        Assert.True((await jan.TryAcquireAsync("jan")).Allowed);
        Assert.True((await anna.TryAcquireAsync("anna")).Allowed);

        var denied = await jan.TryAcquireAsync("jan");
        Assert.False(denied.Allowed);
        Assert.Contains("globalny dzienny limit", denied.Message);
    }

    [Fact]
    public async Task Global_output_chars_budget_is_hard()
    {
        var guard = Guard(new AccessOptions { Enabled = true, MaxGlobalOutputCharsPerDay = 100 },
            limits: null, new FakeTime());

        Assert.True((await guard.TryAcquireAsync("jan")).Allowed);
        await guard.RecordAsync("jan", 150); // przekroczony budżet znaków wyjścia

        var denied = await guard.TryAcquireAsync("jan");
        Assert.False(denied.Allowed);
        Assert.Contains("budżet odpowiedzi", denied.Message);
    }

    [Fact]
    public async Task Capacity_denial_refunds_the_reserved_plan_request()
    {
        // Klient nie może stracić zapytania z pakietu przez ograniczenie po NASZEJ stronie.
        var access = new AccessOptions { Enabled = true, MaxGlobalRequestsPerDay = 0 };
        var guard = Guard(access, Plan(15), new FakeTime());

        var denied = await guard.TryAcquireAsync("konto");

        Assert.False(denied.Allowed);
        var usage = await guard.UsageAsync("konto");
        Assert.Equal(0, usage!.Value.Used);
    }

    [Fact]
    public async Task Capacity_denial_refunds_the_daily_reservation_too()
    {
        var access = new AccessOptions
        {
            Enabled = true, MaxUserRequestsPerDay = 5, MaxGlobalOutputCharsPerDay = 10,
        };
        var counters = new MemoryUsageCounters();
        var guard = Guard(access, limits: null, new FakeTime(), counters);
        await guard.RecordAsync("jan", 50); // budżet znaków już przekroczony

        Assert.False((await guard.TryAcquireAsync("jan")).Allowed);

        var used = await counters.CurrentAsync(UsageScopes.UserRequestsDay, "jan",
            new DateOnly(2026, 7, 8));
        Assert.Equal(0, used);
    }

    // --- zużycie ---

    [Fact]
    public async Task Usage_is_null_without_a_plan_and_counts_with_one()
    {
        var withoutPlan = Guard(new AccessOptions { Enabled = true }, limits: null, new FakeTime());
        Assert.Null(await withoutPlan.UsageAsync("jan"));

        var withPlan = Guard(new AccessOptions { Enabled = false }, Plan(15), new FakeTime());
        await withPlan.TryAcquireAsync("konto");
        var usage = await withPlan.UsageAsync("konto");

        Assert.Equal((1, 15), (usage!.Value.Used, usage.Value.Limit));
    }
}
