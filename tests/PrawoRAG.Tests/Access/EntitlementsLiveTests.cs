using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrawoRAG.Api.Services.Plans;
using PrawoRAG.Storage;
using PrawoRAG.Storage.Entities;

namespace PrawoRAG.Tests.Access;

/// <summary>
/// Uprawnienie czytane z konta w bazie (E1/T-9 + poprawka z E3). Sprawdzamy reguły, które decydują
/// o dostępie płacącego klienta: plan obowiązuje do daty ważności, rezygnacja NIE odbiera dostępu
/// przed końcem opłaconego okresu, a konto bez wpisu (tryb bez kont) nie dostaje limitów planu.
/// </summary>
[Collection("LiveDb")]
public class EntitlementsLiveTests : IAsyncLifetime
{
    private static readonly string Conn =
        Environment.GetEnvironmentVariable("PRAWORAG_DB")
        ?? "Host=localhost;Port=5432;Database=praworag;Username=praworag;Password=praworag";

    private const string UserId = "test-entitlement-konto";
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private readonly ServiceProvider _provider;

    private sealed class FixedTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }

    public EntitlementsLiveTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<PrawoRagDbContext>(o => o.UseNpgsql(Conn, x => x.UseVector()));
        _provider = services.BuildServiceProvider();
    }

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
        await db.Users.Where(u => u.Id == UserId).ExecuteDeleteAsync();
    }

    private async Task SeedAsync(string planId, string status, DateTime? validUntil)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();
        db.Users.Add(new AppUserEntity
        {
            Id = UserId,
            UserName = "entitlement@test.local",
            NormalizedUserName = "ENTITLEMENT@TEST.LOCAL",
            Email = "entitlement@test.local",
            NormalizedEmail = "ENTITLEMENT@TEST.LOCAL",
            SecurityStamp = Guid.NewGuid().ToString(),
            CreatedAtUtc = new DateTime(2026, 5, 4, 8, 0, 0, DateTimeKind.Utc),
            PlanId = planId,
            PlanStatus = status,
            PlanValidUntilUtc = validUntil,
        });
        await db.SaveChangesAsync();
    }

    private IEntitlements Sut() => new Entitlements(
        _provider.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new PlanOptions()),
        new FixedTime(Now));

    [Fact]
    public async Task Paid_plan_applies_while_valid()
    {
        await SeedAsync(PlanIds.Pro, PlanStatuses.Active, Now.AddDays(10));

        var entitlement = await Sut().ForAsync(UserId);

        Assert.Equal(PlanIds.Pro, entitlement.PlanId);
        Assert.Equal(300, entitlement.Limits!.RequestsPerMonth);
    }

    [Fact]
    public async Task Cancelled_subscription_keeps_access_until_the_paid_period_ends()
    {
        // Rezygnacja w połowie okresu: klient zapłacił do końca miesiąca i ma to dostać.
        await SeedAsync(PlanIds.Pro, PlanStatuses.Canceled, Now.AddDays(10));

        var entitlement = await Sut().ForAsync(UserId);

        Assert.Equal(PlanIds.Pro, entitlement.PlanId);
    }

    [Fact]
    public async Task Expired_paid_plan_falls_back_to_free_without_any_background_job()
    {
        await SeedAsync(PlanIds.Pro, PlanStatuses.Active, Now.AddMinutes(-1));

        var entitlement = await Sut().ForAsync(UserId);

        Assert.Equal(PlanIds.Free, entitlement.PlanId);
        Assert.Equal(15, entitlement.Limits!.RequestsPerMonth);
    }

    [Fact]
    public async Task Free_plan_never_expires()
    {
        await SeedAsync(PlanIds.Free, PlanStatuses.Active, validUntil: null);

        var entitlement = await Sut().ForAsync(UserId);

        Assert.Equal(PlanIds.Free, entitlement.PlanId);
        Assert.Equal(15, entitlement.Limits!.RequestsPerMonth);
    }

    [Fact]
    public async Task Without_an_account_no_plan_applies()
    {
        // Tryb sprzed kont (dev, bramka invite): zostają wyłącznie capy pojemności.
        var entitlement = await Sut().ForAsync("konto-ktorego-nie-ma");

        Assert.Null(entitlement.Limits);
        Assert.False(entitlement.PlanApplies);
    }
}
