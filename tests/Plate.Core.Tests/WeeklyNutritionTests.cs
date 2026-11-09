using FluentAssertions;
using Plate.Core.Domain;
using Plate.Core.Nutrition;

namespace Plate.Core.Tests;

public class WeeklyNutritionTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);

    // 80 kg cutting 0.5% at 2,060 kcal: protein minimum 130, fat floor 45 (MacroTargetsTests).
    private static readonly MacroTarget Targets = MacroTargets.Calculate(2060,
        new UserProfile { Goal = Goal.Cut, TargetRatePercentPerWeek = 0.5m, HeightCm = 180, BirthYear = 1996 }, 80m, Today)!;

    private static DaySummary Day(int daysAgo, decimal kcal, decimal protein, decimal fat, decimal? fiber = null, decimal fiberCoverage = 1) =>
        new(Today.AddDays(-daysAgo), new Totals(kcal, protein, 200, fat, fiber), new DayQuality(fiber, fiberCoverage, null, 0));

    [Fact]
    public void Averages_logged_days_and_counts_days_that_reached_the_minimums()
    {
        var week = WeeklyNutrition.Calculate([
            Day(1, 2000, 150, 60, 30),
            Day(2, 2100, 120, 40, 20),
            Day(3, 2200, 135, 50, 25),
            Day(4, 150, 10, 5),          // under 200 kcal: not logged
        ], Targets, Today)!;

        week.LoggedDays.Should().Be(3);
        week.Kcal.Should().Be(2100);
        week.ProteinG.Should().Be(135);
        week.FatG.Should().Be(50);
        week.ProteinMinDays.Should().Be(2);
        week.FatFloorDays.Should().Be(2);
        (week.FiberG, week.FiberDays).Should().Be((25m, 3));
    }

    [Fact]
    public void Fiber_averages_only_days_known_well_enough()
    {
        var week = WeeklyNutrition.Calculate([
            Day(1, 2000, 150, 60, 30),
            Day(2, 2000, 150, 60, 6, fiberCoverage: 0.79m),
            Day(3, 2000, 150, 60, 20, fiberCoverage: 0.8m),
        ], Targets, Today)!;

        (week.FiberG, week.FiberDays).Should().Be((25m, 2));
        week.SaturatedFatG.Should().BeNull("no day had it known");
    }

    [Fact]
    public void Days_outside_the_7_days_before_today_are_ignored()
    {
        var week = WeeklyNutrition.Calculate([Day(0, 3000, 200, 90), Day(1, 2000, 150, 60), Day(2, 2000, 150, 60), Day(7, 2000, 150, 60), Day(8, 9000, 0, 0)], Targets, Today)!;

        week.LoggedDays.Should().Be(3);
        week.Kcal.Should().Be(2000);
    }

    [Fact]
    public void Fewer_than_3_logged_days_show_nothing() =>
        WeeklyNutrition.Calculate([Day(1, 2000, 150, 60), Day(2, 2000, 150, 60), Day(3, 100, 5, 2)], Targets, Today).Should().BeNull();
}
