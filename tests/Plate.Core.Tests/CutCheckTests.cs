using FluentAssertions;
using Plate.Core.Domain;
using Plate.Core.Nutrition;

namespace Plate.Core.Tests;

public class CutCheckTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);

    private static UserProfile Cut(decimal rate = 0.5m, int startedDaysAgo = 30) => new()
    {
        Goal = Goal.Cut, TargetRatePercentPerWeek = rate, HeightCm = 180, BirthYear = 1996, CutStartedOn = Today.AddDays(-startedDaysAgo),
    };

    // A steady trend: one weigh-in a day, losing lossKgPerDay, for 60 days up to today.
    private static List<WeighIn> Losing(decimal lossKgPerWeek) =>
        Enumerable.Range(0, 60).Select(i => new WeighIn(Today.AddDays(i - 59), 90m - lossKgPerWeek / 7 * i)).ToList();

    private static WeekSummary Week(decimal protein) => new(7, 2000, protein, 60, 200, 3, 7, null, 0, null, 0);

    private static IReadOnlyList<CutCheckIn> Checks(UserProfile p, List<WeighIn> weighIns, WeekSummary? week = null) =>
        CutCheck.For(p, Today, weighIns, week, MacroTargets.Calculate(2000, p, TrendWeight.On(weighIns, Today), Today));

    [Fact]
    public void Losing_over_1_percent_a_week_and_over_one_and_a_half_times_the_rate_is_too_fast()
    {
        var check = Checks(Cut(), Losing(1.2m)).Single(c => c.Kind == CutCheckKind.TooFast);

        check.LossPercentPerWeek.Should().BeApproximately(1.4m, 0.1m);
    }

    [Theory]
    [InlineData(0.8, 0.5)]   // under 1% a week
    [InlineData(1.2, 1.0)]   // over 1%, but under 1.5 times a 1% target
    public void Otherwise_the_pace_is_fine(double lossKg, double rate) =>
        Checks(Cut((decimal)rate), Losing((decimal)lossKg)).Should().NotContain(c => c.Kind == CutCheckKind.TooFast);

    [Fact]
    public void The_pace_needs_14_days_of_trend()
    {
        var recent = Losing(1.5m).TakeLast(10).ToList();
        Checks(Cut(), recent).Should().NotContain(c => c.Kind == CutCheckKind.TooFast);
    }

    [Theory]
    [InlineData(129, true)]
    [InlineData(130, false)]
    public void A_weekly_protein_average_under_the_minimum_is_flagged(int protein, bool flagged)
    {
        // 1.6 * 80-ish kg trend gives a 130 g minimum here.
        var weighIns = Losing(0.4m).Select(w => w with { WeightKg = 81m }).ToList();
        Checks(Cut(), weighIns, Week(protein)).Any(c => c.Kind == CutCheckKind.ProteinLow).Should().Be(flagged);
    }

    [Theory]
    [InlineData(83, false)]
    [InlineData(84, true)]
    public void A_break_is_offered_after_12_weeks(int days, bool offered) =>
        Checks(Cut(startedDaysAgo: days), Losing(0.4m)).Any(c => c.Kind == CutCheckKind.BreakOffer).Should().Be(offered);

    [Fact]
    public void Nothing_shows_when_not_cutting_during_a_break_or_while_dismissed()
    {
        var maintain = Cut(startedDaysAgo: 100);
        maintain.Goal = Goal.Maintain;
        Checks(maintain, Losing(1.5m)).Should().BeEmpty();

        var onBreak = Cut(startedDaysAgo: 100);
        onBreak.DietBreakUntil = Today;
        Checks(onBreak, Losing(1.5m)).Should().BeEmpty();

        var dismissed = Cut(startedDaysAgo: 100);
        dismissed.CutCheckDismissedUntil = Today;
        Checks(dismissed, Losing(1.5m)).Should().BeEmpty();
        dismissed.CutCheckDismissedUntil = Today.AddDays(-1);
        Checks(dismissed, Losing(1.5m)).Should().NotBeEmpty("a dismissal hides check-ins through its day only");
    }

    [Fact]
    public void During_a_break_the_target_is_maintenance_with_the_cut_protein_aim()
    {
        var p = Cut();
        p.DietBreakUntil = Today;
        var target = GoalTargets.Calculate(2500, p, 80m, Today);

        target.Kcal.Should().Be(2500);
        target.ProteinG.Should().Be(175, "2.2 g/kg, the cut aim, stays");
        GoalTargets.Calculate(2500, p, 80m, Today.AddDays(1)).Kcal.Should().Be(2060, "the cut resumes the day after");
    }

    [Fact]
    public void Starting_a_break_runs_it_for_14_days_including_today() =>
        CutCheck.BreakUntil(Today).Should().Be(Today.AddDays(13));

    [Fact]
    public void After_a_break_the_cut_restarts_the_day_after_it_ends()
    {
        var p = Cut(startedDaysAgo: 100);
        p.DietBreakUntil = Today.AddDays(-3);

        CutCheck.Sync(p, Today).Should().BeTrue();
        (p.CutStartedOn, p.DietBreakUntil).Should().Be((Today.AddDays(-2), (DateOnly?)null));
        CutCheck.Sync(p, Today).Should().BeFalse("nothing left to change");
    }

    [Fact]
    public void Ending_a_break_early_restarts_the_cut_today()
    {
        var p = Cut(startedDaysAgo: 100);
        p.DietBreakUntil = Today.AddDays(5);

        CutCheck.EndBreak(p, Today);

        (p.CutStartedOn, p.DietBreakUntil).Should().Be((Today, (DateOnly?)null));
    }

    [Fact]
    public void A_cut_without_a_start_date_starts_counting_today()
    {
        var p = Cut();
        p.CutStartedOn = null;

        CutCheck.Sync(p, Today).Should().BeTrue();
        p.CutStartedOn.Should().Be(Today);
    }

    [Fact]
    public void Changing_the_goal_starts_or_clears_the_cut()
    {
        var p = new UserProfile { Goal = Goal.Cut };
        CutCheck.GoalChanged(p, Goal.Maintain, Today);
        p.CutStartedOn.Should().Be(Today);

        p.CutStartedOn = Today.AddDays(-50);
        CutCheck.GoalChanged(p, Goal.Cut, Today);
        p.CutStartedOn.Should().Be(Today.AddDays(-50), "staying on a cut keeps its start");

        p.Goal = Goal.Maintain;
        p.DietBreakUntil = Today.AddDays(3);
        CutCheck.GoalChanged(p, Goal.Cut, Today);
        (p.CutStartedOn, p.DietBreakUntil).Should().Be(((DateOnly?)null, (DateOnly?)null));
    }
}
