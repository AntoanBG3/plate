using FluentAssertions;
using Plate.Core.Domain;
using Plate.Core.Nutrition;
using Plate.Core.Summary;

namespace Plate.Core.Tests;

public class WeeklySummaryTests
{
    private static readonly DateOnly Monday = new(2026, 9, 28);

    private static WeekFacts Facts(int loggedDays = 0, int sessions = 0, int weighIns = 0) => new(
        Monday, Goal.Cut, 0.5m, false,
        loggedDays == 0 ? null : new WeekSummary(loggedDays, 2000, 140, 60, 200, 2, loggedDays, null, 0, null, 0),
        null, null, weighIns, sessions, 0, [], []);

    [Theory]
    [InlineData(3, 0, 0, true)]
    [InlineData(2, 0, 0, false)]
    [InlineData(0, 1, 0, true)]
    [InlineData(0, 0, 3, true)]
    [InlineData(0, 0, 2, false)]
    [InlineData(2, 0, 2, false)]
    public void There_is_something_to_say_with_3_logged_days_a_workout_or_3_weigh_ins(int days, int sessions, int weighIns, bool enough) =>
        Facts(days, sessions, weighIns).HasEnough.Should().Be(enough);

    [Fact]
    public void The_answer_is_trimmed_to_size_and_empty_tips_are_dropped()
    {
        var text = WeeklySummaryText.Clean(" " + new string('a', 450) + " ", ["  Eat protein at breakfast. ", "", "   ", new string('b', 200), "Third", "Fourth"], " Keep going! ")!;

        text.Summary.Should().HaveLength(400).And.EndWith("…");
        text.Tips.Should().HaveCount(3);
        text.Tips[0].Should().Be("Eat protein at breakfast.");
        text.Tips[1].Should().HaveLength(160);
        text.Tips[2].Should().Be("Third");
        text.Motivation.Should().Be("Keep going!");
    }

    [Fact]
    public void An_empty_summary_is_no_answer() =>
        WeeklySummaryText.Clean("  ", ["Tip"], "Go").Should().BeNull();

    [Fact]
    public void Weekly_nutrition_can_cover_a_given_week()
    {
        var targets = MacroTargets.Calculate(2000, new UserProfile { Goal = Goal.Maintain }, 80m, Monday)!;
        DaySummary Day(DateOnly d, decimal kcal) => new(d, new Totals(kcal, 150, 200, 60), new DayQuality(null, 0, null, 0));

        var week = WeeklyNutrition.Between(
            [Day(Monday.AddDays(-1), 9000), Day(Monday, 2000), Day(Monday.AddDays(3), 2100), Day(Monday.AddDays(6), 2200), Day(Monday.AddDays(7), 9000)],
            targets, Monday, Monday.AddDays(7))!;

        (week.LoggedDays, week.Kcal).Should().Be((3, 2100m));
    }
}
