using FluentAssertions;
using GymTracker.Core.Analytics;
using GymTracker.Core.Domain;
using GymTracker.Core.Training;

namespace GymTracker.Core.Tests;

public class OneRepMaxTests
{
    [Theory]
    [InlineData(100, 1, 100)]
    [InlineData(100, 10, 133.333)]
    [InlineData(80, 12, 112)]
    [InlineData(60, 5, 70)]
    public void Uses_the_Epley_formula(decimal weight, int reps, decimal expected) =>
        OneRepMax.Estimate(weight, reps)!.Value.Should().BeApproximately(expected, 0.001m);

    [Theory]
    [InlineData(100, 0)]
    [InlineData(100, 13)]
    [InlineData(0, 5)]
    public void Ignores_sets_outside_one_to_twelve_reps_or_without_weight(decimal weight, int reps) =>
        OneRepMax.Estimate(weight, reps).Should().BeNull();

    [Fact]
    public void Best_of_a_session_skips_warmups_and_high_rep_sets()
    {
        var sets = new List<LoggedSet>
        {
            new(120, 3, IsWarmup: true),
            new(80, 10),   // 106.67
            new(85, 8),    // 107.67
            new(60, 20),   // ignored
        };

        OneRepMax.Best(sets)!.Value.Should().BeApproximately(107.667m, 0.001m);
        OneRepMax.Best([new LoggedSet(50, 15)]).Should().BeNull();
    }
}

public class WeeklyVolumeTests
{
    [Fact]
    public void Counts_primary_as_one_and_each_secondary_as_a_half()
    {
        var day = new DateOnly(2026, 9, 30);
        var sets = new[]
        {
            new VolumeSet(day, Muscle.Chest, [Muscle.Triceps, Muscle.Shoulders]),
            new VolumeSet(day, Muscle.Chest, [Muscle.Triceps, Muscle.Shoulders]),
            new VolumeSet(day, Muscle.Triceps, []),
        };

        var week = WeeklyVolume.Calculate(sets)[IsoWeek.Of(day)];

        week[Muscle.Chest].Should().Be(2m);
        week[Muscle.Triceps].Should().Be(2m);
        week[Muscle.Shoulders].Should().Be(1m);
    }

    [Fact]
    public void Splits_by_iso_week_with_monday_starts()
    {
        var sunday = new DateOnly(2026, 10, 4);
        var monday = new DateOnly(2026, 10, 5);
        var result = WeeklyVolume.Calculate([new VolumeSet(sunday, Muscle.Back, []), new VolumeSet(monday, Muscle.Back, [])]);

        result.Should().HaveCount(2);
        result[IsoWeek.Of(sunday)][Muscle.Back].Should().Be(1m);
        IsoWeek.Of(monday).Should().Be(new IsoWeek(2026, 41));
        IsoWeek.Of(monday).Previous().Should().Be(IsoWeek.Of(sunday));
    }

    [Fact]
    public void Iso_week_across_new_year_belongs_to_the_right_year()
    {
        // 2027-01-01 is a Friday, in ISO week 53 of 2026.
        IsoWeek.Of(new DateOnly(2027, 1, 1)).Should().Be(new IsoWeek(2026, 53));
    }

    [Fact]
    public void Ignores_warmups_and_duplicate_or_primary_secondaries()
    {
        var day = new DateOnly(2026, 9, 30);
        var result = WeeklyVolume.Calculate(
        [
            new VolumeSet(day, Muscle.Quads, [Muscle.Glutes, Muscle.Glutes, Muscle.Quads]),
            new VolumeSet(day, Muscle.Quads, [Muscle.Glutes], IsWarmup: true),
        ])[IsoWeek.Of(day)];

        result[Muscle.Quads].Should().Be(1m);
        result[Muscle.Glutes].Should().Be(0.5m);
    }
}
