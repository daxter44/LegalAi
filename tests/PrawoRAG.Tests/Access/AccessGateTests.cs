using Microsoft.Extensions.Options;
using PrawoRAG.Api.Services;

namespace PrawoRAG.Tests.Access;

/// <summary>
/// Bramka dostępu 3.7 (czyste, bez HTTP): rozpoznawanie kodów zaproszeń + twardy dzienny cap kosztów
/// LLM. Kluczowe niezmienniki: Enabled=false = zero zmian zachowania (dev/M4); rollover dnia zeruje
/// liczniki (FakeTimeProvider — bez czekania do północy); reason wskazuje, KTÓRY limit padł.
/// </summary>
public class AccessGateTests
{
    // --- AccessOptions.TryResolveInvite ---

    [Fact]
    public void Invite_resolves_with_trim()
    {
        var o = new AccessOptions { Invites = { ["kod123"] = "Jan Kowalski" } };

        Assert.True(o.TryResolveInvite("  kod123 ", out var name));
        Assert.Equal("Jan Kowalski", name);
    }

    [Fact]
    public void Invalid_or_empty_code_is_rejected()
    {
        var o = new AccessOptions { Invites = { ["kod123"] = "Jan" } };

        Assert.False(o.TryResolveInvite("zly-kod", out _));
        Assert.False(o.TryResolveInvite("", out _));
        Assert.False(o.TryResolveInvite(null, out _));
        Assert.False(o.TryResolveInvite("KOD123", out _)); // case-sensitive
    }

    // --- CostGuard ---

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static CostGuard Guard(AccessOptions o, FakeTime time) => new(Options.Create(o), time);

    [Fact]
    public void Disabled_gate_always_allows()
    {
        var guard = Guard(new AccessOptions { Enabled = false, MaxUserRequestsPerDay = 0 }, new FakeTime());

        Assert.True(guard.TryAcquire("ktokolwiek", out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void User_daily_limit_is_hard()
    {
        var guard = Guard(new AccessOptions { Enabled = true, MaxUserRequestsPerDay = 2 }, new FakeTime());

        Assert.True(guard.TryAcquire("jan", out _));
        Assert.True(guard.TryAcquire("jan", out _));
        Assert.False(guard.TryAcquire("jan", out var reason));
        Assert.Contains("Twój dzienny limit", reason);
        Assert.True(guard.TryAcquire("anna", out _)); // limit per OSOBA — inny tester wchodzi
    }

    [Fact]
    public void Global_daily_request_limit_is_hard()
    {
        var guard = Guard(new AccessOptions { Enabled = true, MaxGlobalRequestsPerDay = 2 }, new FakeTime());

        Assert.True(guard.TryAcquire("jan", out _));
        Assert.True(guard.TryAcquire("anna", out _));
        Assert.False(guard.TryAcquire("piotr", out var reason)); // globalny — niezależnie od usera
        Assert.Contains("globalny dzienny limit", reason);
    }

    [Fact]
    public void Global_output_chars_budget_is_hard()
    {
        var guard = Guard(new AccessOptions { Enabled = true, MaxGlobalOutputCharsPerDay = 100 }, new FakeTime());

        Assert.True(guard.TryAcquire("jan", out _));
        guard.Record("jan", 150); // przekroczony budżet znaków wyjścia
        Assert.False(guard.TryAcquire("jan", out var reason));
        Assert.Contains("budżet odpowiedzi", reason);
    }

    [Fact]
    public void Day_rollover_resets_counters()
    {
        var time = new FakeTime();
        var guard = Guard(new AccessOptions
        {
            Enabled = true, MaxUserRequestsPerDay = 1, MaxGlobalOutputCharsPerDay = 100,
        }, time);

        Assert.True(guard.TryAcquire("jan", out _));
        guard.Record("jan", 150);
        Assert.False(guard.TryAcquire("jan", out _)); // limity wyczerpane

        time.Now = time.Now.AddDays(1); // północ UTC minęła
        Assert.True(guard.TryAcquire("jan", out _)); // liczniki od zera
    }
}
