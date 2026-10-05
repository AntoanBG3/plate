using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;

namespace GymTracker.Core.Tests;

public class CoverageTests
{
    private static FoodLogEntry Entry(decimal kcal, decimal? fiber, decimal? saturatedFat = null) =>
        new() { Kcal = kcal, FiberG = fiber, SaturatedFatG = saturatedFat };

    [Fact]
    public void Coverage_is_the_share_of_calories_from_entries_with_a_value()
    {
        var q = DayQuality.Of([Entry(800, 6, 3), Entry(200, null, 1)]);

        (q.FiberG, q.FiberCoverage, q.FiberComplete).Should().Be((6m, 0.8m, true));
        (q.SaturatedFatG, q.SaturatedFatCoverage, q.SaturatedFatComplete).Should().Be((4m, 1m, true));
    }

    [Fact]
    public void Just_under_80_percent_is_incomplete() =>
        DayQuality.Of([Entry(799, 6), Entry(201, null)]).FiberComplete.Should().BeFalse();

    [Fact]
    public void Nothing_known_is_null_not_zero()
    {
        var q = DayQuality.Of([Entry(500, null)]);

        q.FiberG.Should().BeNull();
        q.FiberCoverage.Should().Be(0);
    }

    [Fact]
    public void An_empty_day_has_nothing_missing() =>
        DayQuality.Of([]).Should().Be(new DayQuality(null, 1, null, 1));
}
