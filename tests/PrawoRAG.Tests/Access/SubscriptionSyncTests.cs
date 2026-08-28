using PrawoRAG.Api.Services.Billing;
using PrawoRAG.Api.Services.Plans;
using PrawoRAG.Storage.Entities;

namespace PrawoRAG.Tests.Access;

/// <summary>
/// Stany subskrypcji → uprawnienie konta (E3/US-3.1). To jest reguła, która decyduje, czy płacący
/// klient ma dostęp, więc sprawdzamy dokładnie te przypadki, w których takie wdrożenia się wykładają:
/// zdarzenia nie po kolei, odrzucona karta, rezygnacja w połowie okresu, wygaśnięcie.
/// </summary>
public class SubscriptionSyncTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private static BillingOptions Options(int graceDays = 7) =>
        new() { PaidPlanId = PlanIds.Pro, GraceDays = graceDays };

    private static AppUserEntity FreeAccount() => new()
    {
        Id = "konto-1",
        PlanId = PlanIds.Free,
        PlanStatus = PlanStatuses.Active,
        CreatedAtUtc = new DateTime(2026, 5, 4, 8, 0, 0, DateTimeKind.Utc),
    };

    private static SubscriptionState State(
        string status, DateTime? periodEnd = null, bool cancelAtPeriodEnd = false,
        DateTime? eventTime = null, bool deleted = false, DateTime? periodStart = null) =>
        new("cus_1", "sub_1", status, periodStart ?? Now, periodEnd ?? Now.AddMonths(1),
            cancelAtPeriodEnd, eventTime ?? Now, deleted);

    [Fact]
    public void Active_subscription_grants_the_paid_plan_until_period_end()
    {
        var user = FreeAccount();
        var periodEnd = Now.AddMonths(1);

        Assert.True(SubscriptionSync.Apply(user, State("active", periodEnd), Options(), Now));

        Assert.Equal(PlanIds.Pro, user.PlanId);
        Assert.Equal(PlanStatuses.Active, user.PlanStatus);
        Assert.Equal(periodEnd, user.PlanValidUntilUtc);
        Assert.Equal("cus_1", user.StripeCustomerId);
        Assert.Equal("sub_1", user.StripeSubscriptionId);
    }

    [Fact]
    public void Billing_anchor_follows_the_subscription_period()
    {
        // Limit planu ma odnawiać się RAZEM z płatnością, nie w dniu rejestracji konta.
        var user = FreeAccount();
        var periodStart = new DateTime(2026, 8, 20, 9, 30, 0, DateTimeKind.Utc);

        SubscriptionSync.Apply(user, State("active", periodStart: periodStart), Options(), Now);

        Assert.Equal(periodStart, user.BillingAnchorUtc);
    }

    [Fact]
    public void Past_due_keeps_access_for_the_grace_period()
    {
        // Odrzucona karta to nie rezygnacja — Stripe ponawia obciążenie przez kilka dni.
        var user = FreeAccount();
        var periodEnd = Now.AddDays(1);

        SubscriptionSync.Apply(user, State("past_due", periodEnd), Options(graceDays: 7), Now);

        Assert.Equal(PlanIds.Pro, user.PlanId);
        Assert.Equal(PlanStatuses.PastDue, user.PlanStatus);
        Assert.Equal(periodEnd.AddDays(7), user.PlanValidUntilUtc);
    }

    [Fact]
    public void Cancel_at_period_end_keeps_access_until_the_paid_period_ends()
    {
        // Rezygnacja znaczy „nie przedłużaj", a nie „zabierz to, co opłacone".
        var user = FreeAccount();
        var periodEnd = Now.AddDays(20);

        SubscriptionSync.Apply(user, State("active", periodEnd, cancelAtPeriodEnd: true), Options(), Now);

        Assert.Equal(PlanIds.Pro, user.PlanId);
        Assert.Equal(PlanStatuses.Canceled, user.PlanStatus);
        Assert.Equal(periodEnd, user.PlanValidUntilUtc);
    }

    [Fact]
    public void Deleted_subscription_drops_the_account_to_free_immediately()
    {
        var user = FreeAccount();
        SubscriptionSync.Apply(user, State("active"), Options(), Now);

        SubscriptionSync.Apply(user, State("canceled", eventTime: Now.AddMinutes(1), deleted: true), Options(), Now);

        Assert.Equal(PlanIds.Free, user.PlanId);
        Assert.Null(user.PlanValidUntilUtc);
        Assert.Null(user.StripeSubscriptionId);
        Assert.Equal("cus_1", user.StripeCustomerId); // klient zostaje — może wrócić
    }

    [Fact]
    public void Incomplete_first_payment_does_not_grant_the_plan()
    {
        // 3D Secure w toku: pieniądze jeszcze nie przeszły, więc planu nie ma.
        var user = FreeAccount();

        SubscriptionSync.Apply(user, State("incomplete"), Options(), Now);

        Assert.Equal(PlanIds.Free, user.PlanId);
        Assert.Null(user.PlanValidUntilUtc);
    }

    [Fact]
    public void Stale_event_is_ignored()
    {
        // SEDNO US-3.5: spóźnione „anulowano" nie może wyłączyć konta, które właśnie się przedłużyło.
        var user = FreeAccount();
        var renewal = Now;
        SubscriptionSync.Apply(user, State("active", eventTime: renewal), Options(), Now);

        var applied = SubscriptionSync.Apply(user,
            State("canceled", eventTime: renewal.AddMinutes(-5), deleted: true), Options(), Now);

        Assert.False(applied);
        Assert.Equal(PlanIds.Pro, user.PlanId);       // plan nietknięty
        Assert.Equal(renewal, user.PlanUpdatedAtUtc); // znacznik też
    }

    [Fact]
    public void Newer_event_is_applied_after_an_older_one()
    {
        var user = FreeAccount();
        SubscriptionSync.Apply(user, State("active", eventTime: Now), Options(), Now);

        var applied = SubscriptionSync.Apply(user,
            State("past_due", eventTime: Now.AddMinutes(5)), Options(), Now);

        Assert.True(applied);
        Assert.Equal(PlanStatuses.PastDue, user.PlanStatus);
    }

    [Fact]
    public void Unknown_status_leaves_the_entitlement_untouched()
    {
        // Stripe może dodać status, którego dziś nie znamy — nie zgadujemy w żadną stronę.
        var user = FreeAccount();
        SubscriptionSync.Apply(user, State("active"), Options(), Now);
        var before = (user.PlanId, user.PlanStatus, user.PlanValidUntilUtc);

        SubscriptionSync.Apply(user, State("cos_nowego", eventTime: Now.AddMinutes(1)), Options(), Now);

        Assert.Equal(before, (user.PlanId, user.PlanStatus, user.PlanValidUntilUtc));
    }
}
