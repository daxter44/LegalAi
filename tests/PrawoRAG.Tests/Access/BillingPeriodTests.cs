using PrawoRAG.Api.Services.Plans;

namespace PrawoRAG.Tests.Access;

/// <summary>
/// Okres rozliczeniowy (E1/T-8) — liczony od DNIA MIESIĄCA założenia konta, nie od pierwszego
/// kalendarzowego. Testy pilnują trzech rzeczy, na których łatwo się przejechać: dni brzegowych
/// (29-31), granicy roku i konta młodszego niż jeden okres.
/// </summary>
public class BillingPeriodTests
{
    private static DateTime Utc(int y, int m, int d, int h = 12) => new(y, m, d, h, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Period_runs_from_signup_day_to_same_day_next_month()
    {
        var anchor = Utc(2026, 3, 10);

        var period = BillingPeriodCalculator.Current(anchor, Utc(2026, 5, 20));

        Assert.Equal(Utc(2026, 5, 10), period.StartUtc);
        Assert.Equal(Utc(2026, 6, 10), period.EndUtc);
    }

    [Fact]
    public void Before_anchor_day_current_period_started_previous_month()
    {
        var anchor = Utc(2026, 3, 20);

        // 5 maja: dzień kotwicy (20.) jeszcze nie nadszedł, więc trwa okres od 20 kwietnia.
        var period = BillingPeriodCalculator.Current(anchor, Utc(2026, 5, 5));

        Assert.Equal(Utc(2026, 4, 20), period.StartUtc);
        Assert.Equal(Utc(2026, 5, 20), period.EndUtc);
    }

    [Fact]
    public void Anchor_on_31st_survives_short_months()
    {
        var anchor = Utc(2026, 1, 31);

        // Luty 2026 ma 28 dni — okres zaczyna się ostatniego dnia lutego, nie „przeskakuje" do marca.
        var february = BillingPeriodCalculator.Current(anchor, Utc(2026, 2, 28, 20));
        Assert.Equal(Utc(2026, 2, 28), february.StartUtc);
        Assert.Equal(Utc(2026, 3, 31), february.EndUtc);

        // I wraca na 31. w miesiącu, który to unosi.
        var march = BillingPeriodCalculator.Current(anchor, Utc(2026, 4, 2));
        Assert.Equal(Utc(2026, 3, 31), march.StartUtc);
        Assert.Equal(Utc(2026, 4, 30), march.EndUtc);
    }

    [Fact]
    public void Period_crosses_year_boundary()
    {
        var anchor = Utc(2025, 12, 15);

        var period = BillingPeriodCalculator.Current(anchor, Utc(2026, 1, 3));

        Assert.Equal(Utc(2025, 12, 15), period.StartUtc);
        Assert.Equal(Utc(2026, 1, 15), period.EndUtc);
    }

    [Fact]
    public void Fresh_account_counts_from_signup_not_from_before_it_existed()
    {
        var anchor = Utc(2026, 5, 20, 9);

        var period = BillingPeriodCalculator.Current(anchor, Utc(2026, 5, 20, 18));

        Assert.Equal(anchor, period.StartUtc);
        Assert.Equal(Utc(2026, 6, 20, 9), period.EndUtc);
    }

    [Fact]
    public void Period_key_changes_between_periods_so_counter_resets_by_itself()
    {
        var anchor = Utc(2026, 3, 10);

        var may = BillingPeriodCalculator.Current(anchor, Utc(2026, 5, 20)).Key;
        var june = BillingPeriodCalculator.Current(anchor, Utc(2026, 6, 20)).Key;

        Assert.NotEqual(may, june); // inny klucz = inny wiersz licznika = zero bez zadania w tle
    }

    // --- katalog planów ---

    [Fact]
    public void Unknown_plan_falls_back_to_default_not_to_unlimited()
    {
        var plans = new PlanOptions();

        var limits = plans.Resolve("plan-ktorego-nie-ma");

        Assert.Equal(15, limits.RequestsPerMonth); // darmowy, NIE brak limitu
    }

    [Fact]
    public void Configured_plans_carry_agreed_values()
    {
        var plans = new PlanOptions();

        Assert.Equal(15, plans.Resolve(PlanIds.Free).RequestsPerMonth);
        Assert.Equal(300, plans.Resolve(PlanIds.Pro).RequestsPerMonth);
    }
}
