using FluentAssertions;
using Plate.Core.Domain;
using Plate.Core.Nutrition;

namespace Plate.Core.Tests;

public class TrendWeightTests
{
    private static readonly DateOnly Day1 = new(2026, 1, 1);

    [Fact]
    public void Is_empty_without_weigh_ins() =>
        TrendWeight.Series([], Day1).Should().BeEmpty();

    [Fact]
    public void Is_seeded_with_the_first_weigh_in_and_moves_a_tenth_of_the_way_each_day()
    {
        var series = TrendWeight.Series([new(Day1, 80m), new(Day1.AddDays(1), 81m), new(Day1.AddDays(2), 79m)], Day1.AddDays(2));

        series.Select(p => p.TrendKg).Should().Equal(80m, 80.1m, 79.99m);
        series.Select(p => p.RawKg).Should().Equal(80m, 81m, 79m);
    }

    [Fact]
    public void Carries_the_trend_forward_on_days_without_a_weigh_in()
    {
        var series = TrendWeight.Series([new(Day1, 80m), new(Day1.AddDays(3), 84m)], Day1.AddDays(5));

        series.Should().HaveCount(6);
        series.Select(p => p.TrendKg).Should().Equal(80m, 80m, 80m, 80.4m, 80.4m, 80.4m);
        series[1].RawKg.Should().BeNull();
    }

    [Fact]
    public void Starts_at_the_first_weigh_in_and_ignores_later_ones()
    {
        var series = TrendWeight.Series([new(Day1.AddDays(2), 70m), new(Day1.AddDays(9), 90m)], Day1.AddDays(4));

        series.Select(p => p.Date).Should().Equal(Day1.AddDays(2), Day1.AddDays(3), Day1.AddDays(4));
        series.Should().OnlyContain(p => p.TrendKg == 70m);
    }

    [Fact]
    public void On_returns_null_before_the_first_weigh_in()
    {
        TrendWeight.On([new(Day1.AddDays(1), 70m)], Day1).Should().BeNull();
        TrendWeight.On([new(Day1, 70m)], Day1.AddDays(3)).Should().Be(70m);
    }
}

public class AdaptiveMaintenanceTests
{
    private static readonly DateOnly Start = new(2026, 1, 1);

    /// <summary>Builds daily weigh-ins and intake from day 0 up to (not including) <paramref name="days"/>.</summary>
    private sealed class History
    {
        public List<WeighIn> WeighIns { get; } = [];
        public Dictionary<DateOnly, decimal> Intake { get; } = [];

        public History Days(int from, int to, Func<int, decimal?> weight, Func<int, decimal?> kcal)
        {
            for (var d = from; d < to; d++)
            {
                var date = Start.AddDays(d);
                if (weight(d) is { } w)
                {
                    WeighIns.Add(new WeighIn(date, w));
                }

                if (kcal(d) is { } k)
                {
                    Intake[date] = k;
                }
            }

            return this;
        }

        public MaintenanceResult? On(int day, int? prior, int? lastAdaptive = null) =>
            AdaptiveMaintenance.Calculate(new MaintenanceInput(Start.AddDays(day), prior, lastAdaptive, WeighIns, Intake));
    }

    [Fact]
    public void Stable_weight_at_a_known_intake_returns_that_intake()
    {
        var history = new History().Days(0, 21, _ => 80m, _ => 2400m);

        var result = history.On(21, prior: 2400)!;

        result.TdeeKcal.Should().Be(2400);
        result.Method.Should().Be(MaintenanceMethod.Adaptive);
        result.DaysOfData.Should().Be(21);
        result.LoggingCompleteness.Should().Be(1m);
        result.LowConfidence.Should().BeFalse();
    }

    [Fact]
    public void Stable_weight_converges_on_the_intake_from_a_wrong_prior()
    {
        var history = new History().Days(0, 21 + 7 * 12, _ => 80m, _ => 2400m);

        var estimates = WeeklyUpdates(history, prior: 2900, weeks: 12);

        estimates[0].Should().Be(2800, "the first update is clamped to 100 kcal");
        estimates[^1].Should().BeInRange(2390, 2410);
    }

    [Fact]
    public void Steady_loss_of_half_a_kilo_a_week_at_2000_kcal_converges_near_2550()
    {
        var history = new History().Days(0, 21 + 7 * 16, d => 90m - 0.5m * d / 7m, _ => 2000m);

        var estimates = WeeklyUpdates(history, prior: 2000, weeks: 16);

        estimates.Should().BeInAscendingOrder();
        estimates[^1].Should().BeInRange(2530, 2570);
    }

    [Fact]
    public void Noisy_daily_weights_never_move_the_estimate_more_than_the_clamp()
    {
        var random = new Random(42);
        var history = new History().Days(0, 21 + 7 * 10, _ => 80m + (decimal)(random.NextDouble() * 3 - 1.5), _ => 2500m);

        var estimates = WeeklyUpdates(history, prior: 2500, weeks: 10);

        estimates.Prepend(2500).Zip(estimates.Skip(0), (a, b) => Math.Abs(b - a))
            .Should().OnlyContain(change => change <= AdaptiveMaintenance.MaxWeeklyChangeKcal);
        estimates.Should().OnlyContain(e => e >= 2350 && e <= 2650);
    }

    [Fact]
    public void Logging_below_80_percent_carries_the_last_adaptive_estimate_forward()
    {
        // Food on 16 of 21 days is 76%.
        var history = new History().Days(0, 21, _ => 80m, d => d % 4 == 0 && d < 20 ? null : 2400m);

        var withAdaptive = history.On(21, prior: 2200, lastAdaptive: 2600)!;
        withAdaptive.Method.Should().Be(MaintenanceMethod.Initial);
        withAdaptive.TdeeKcal.Should().Be(2600);
        withAdaptive.LoggingCompleteness.Should().BeApproximately(16m / 21m, 0.0001m);

        history.On(21, prior: 2200)!.TdeeKcal.Should().Be(2200, "with no adaptive estimate yet, the prior is carried");
    }

    [Fact]
    public void Days_under_200_kcal_count_as_unlogged()
    {
        // 17 of 21 days is 81%, which passes; one of them at 150 kcal leaves 16 (76%), which fails.
        var passing = new History().Days(0, 21, _ => 80m, d => d is 2 or 7 or 12 or 17 ? null : 2400m);
        passing.On(21, prior: 2400)!.Method.Should().Be(MaintenanceMethod.Adaptive);

        var failing = new History().Days(0, 21, _ => 80m, d => d is 2 or 7 or 12 or 17 ? null : d == 5 ? 150m : 2400m);
        failing.On(21, prior: 2400)!.Method.Should().Be(MaintenanceMethod.Initial);
    }

    [Fact]
    public void Days_under_200_kcal_are_left_out_of_the_average_intake()
    {
        // Three near-empty days among 21: if they were averaged in, intake would look 300 kcal lower.
        var history = new History().Days(0, 21, _ => 80m, d => d is 3 or 10 or 16 ? 100m : 2400m);

        history.On(21, prior: 2400)!.TdeeKcal.Should().Be(2400);
    }

    [Fact]
    public void Weigh_ins_on_fewer_than_half_the_days_fail_the_check()
    {
        // Weigh-ins every third day: 7 of 21.
        var history = new History().Days(0, 21, d => d % 3 == 0 ? 80m : null, _ => 2400m);

        var result = history.On(21, prior: 2500)!;

        result.Method.Should().Be(MaintenanceMethod.Initial);
        result.TdeeKcal.Should().Be(2500);
        result.LowConfidence.Should().BeFalse("the gaps are only 2 days long");
    }

    [Fact]
    public void Fewer_than_14_days_since_the_first_weigh_in_is_not_enough()
    {
        var history = new History().Days(0, 13, _ => 80m, _ => 2400m);

        var result = history.On(13, prior: 2500)!;

        result.Method.Should().Be(MaintenanceMethod.Initial);
        result.DaysOfData.Should().Be(13, "the window ends yesterday and starts at the first weigh-in");
    }

    [Fact]
    public void Fourteen_days_is_enough_and_the_window_starts_at_the_first_weigh_in()
    {
        var history = new History().Days(0, 14, _ => 80m, _ => 2400m);

        var result = history.On(14, prior: 2400)!;

        result.Method.Should().Be(MaintenanceMethod.Adaptive);
        result.DaysOfData.Should().Be(14);
    }

    [Fact]
    public void The_window_is_the_21_days_ending_yesterday()
    {
        // Days 0 to 9 at a very different intake fall outside the window and must not count; today (day 31) is ignored too.
        var history = new History().Days(0, 32, _ => 80m, d => d < 10 || d == 31 ? 5000m : 2400m);

        history.On(31, prior: 2400)!.TdeeKcal.Should().Be(2400);
    }

    [Fact]
    public void A_gap_of_three_days_without_a_weigh_in_is_low_confidence_but_still_calculated()
    {
        var history = new History().Days(0, 21, d => d is 8 or 9 or 10 ? null : 80m, _ => 2400m);

        var result = history.On(21, prior: 2400)!;

        result.LowConfidence.Should().BeTrue();
        result.Method.Should().Be(MaintenanceMethod.Adaptive);
        result.TdeeKcal.Should().Be(2400);
    }

    [Fact]
    public void Without_a_prior_or_an_adaptive_estimate_there_is_nothing_to_return()
    {
        var history = new History().Days(0, 21, _ => 80m, _ => 2400m);

        history.On(21, prior: null).Should().BeNull();
        history.On(21, prior: null, lastAdaptive: 2450)!.Method.Should().Be(MaintenanceMethod.Adaptive);
    }

    [Fact]
    public void Without_weigh_ins_the_prior_is_carried()
    {
        var history = new History().Days(0, 21, _ => null, _ => 2400m);

        var result = history.On(21, prior: 2300)!;

        result.Should().Be(new MaintenanceResult(2300, MaintenanceMethod.Initial, 0, 0, false));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, false)]
    [InlineData(6, false)]
    [InlineData(7, true)]
    [InlineData(30, true)]
    public void A_new_estimate_is_due_weekly(int? daysSinceLast, bool due)
    {
        var today = new DateOnly(2026, 3, 10);
        DateOnly? last = daysSinceLast is { } d ? today.AddDays(-d) : null;

        AdaptiveMaintenance.IsDue(last, today).Should().Be(due);
    }

    /// <summary>Runs one update a week from day 21, feeding each estimate into the next, as the app does.</summary>
    private static List<int> WeeklyUpdates(History history, int prior, int weeks)
    {
        var estimates = new List<int>();
        int? last = null;
        for (var week = 0; week < weeks; week++)
        {
            var result = history.On(21 + week * 7, prior, last)!;
            result.Method.Should().Be(MaintenanceMethod.Adaptive);
            last = result.TdeeKcal;
            estimates.Add(result.TdeeKcal);
        }

        return estimates;
    }
}

public class GoalTargetsTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);

    private static UserProfile Profile(Goal goal, decimal rate = 0, Sex? sex = Sex.Male) =>
        new() { Goal = goal, TargetRatePercentPerWeek = rate, Sex = sex, ProteinGramsPerKg = 2.0m };

    [Fact]
    public void Maintain_targets_the_maintenance_estimate() =>
        GoalTargets.Calculate(2500, Profile(Goal.Maintain), 80m, Today).Should().BeEquivalentTo(new { Kcal = (int?)2500, ProteinG = (int?)160, FloorApplied = false });

    [Fact]
    public void A_cut_subtracts_the_rate_times_trend_weight_in_kcal_per_day() =>
        // 80 kg * 0.5% = 0.4 kg/week * 7700 / 7 = 440 kcal/day
        GoalTargets.Calculate(2500, Profile(Goal.Cut, 0.5m), 80m, Today).Kcal.Should().Be(2060);

    [Fact]
    public void A_bulk_adds_it() =>
        // 80 kg * 0.25% = 0.2 kg/week = 220 kcal/day
        GoalTargets.Calculate(2500, Profile(Goal.Bulk, 0.25m), 80m, Today).Kcal.Should().Be(2720);

    [Theory]
    [InlineData(Sex.Male, 1800)]
    [InlineData(Sex.Female, 1500)]
    [InlineData(null, 1500)]
    public void The_target_never_goes_below_the_floor(Sex? sex, int floor)
    {
        // 1.0% of 90 kg is 990 kcal/day off a 2000 kcal maintenance.
        var target = GoalTargets.Calculate(2000, Profile(Goal.Cut, 1.0m, sex), 90m, Today);

        target.Kcal.Should().Be(floor);
        target.FloorApplied.Should().BeTrue();
    }

    [Fact]
    public void A_target_above_the_floor_is_left_alone() =>
        GoalTargets.Calculate(2300, Profile(Goal.Cut, 0.5m, Sex.Female), 60m, Today).Should().BeEquivalentTo(new { Kcal = (int?)1970, ProteinG = (int?)120, FloorApplied = false });

    [Fact]
    public void The_floor_applies_to_maintenance_too() =>
        GoalTargets.Calculate(1400, Profile(Goal.Maintain, sex: Sex.Female), 50m, Today).Should().BeEquivalentTo(new { Kcal = (int?)1500, ProteinG = (int?)100, FloorApplied = true });

    [Fact]
    public void A_cut_or_bulk_needs_a_trend_weight()
    {
        GoalTargets.Calculate(2500, Profile(Goal.Cut, 0.5m), null, Today).Should().BeEquivalentTo(new { Kcal = (int?)null, ProteinG = (int?)null, FloorApplied = false });
        GoalTargets.Calculate(2500, Profile(Goal.Maintain), null, Today).Should().BeEquivalentTo(new { Kcal = (int?)2500, ProteinG = (int?)null, FloorApplied = false });
    }

    [Fact]
    public void Protein_uses_the_trend_weight_rounded_to_5_grams() =>
        GoalTargets.Calculate(2500, Profile(Goal.Maintain), 82.1m, Today).ProteinG.Should().Be(165);

    [Fact]
    public void Macros_are_computed_from_the_target_after_the_floor()
    {
        var target = GoalTargets.Calculate(1400, Profile(Goal.Maintain, sex: Sex.Female), 50m, Today);

        target.Macros!.FiberMinG.Should().Be(25);
        target.Macros.SaturatedFatMaxG.Should().Be(17, "10% of 1,500 kcal / 9 = 16.7");
        target.ProteinG.Should().Be(target.Macros.ProteinAimG);
    }

    [Fact]
    public void Without_a_trend_weight_there_are_no_macros_but_auto_protein_needs_one_too() =>
        GoalTargets.Calculate(2500, new UserProfile { Goal = Goal.Maintain }, null, Today).Macros.Should().BeNull();
}
