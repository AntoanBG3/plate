using FluentAssertions;
using GymTracker.Core.Training;

namespace GymTracker.Core.Tests;

public class SetRulesTests
{
    [Theory]
    [InlineData(0, 10, null)]
    [InlineData(82.5, 8, null)]
    [InlineData(1.25, 0, null)]
    [InlineData(100, 5, 8.5)]
    [InlineData(100, 5, 10.0)]
    public void Accepts_valid_sets(decimal weight, int reps, double? rpe) =>
        SetRules.Validate(weight, reps, (decimal?)rpe).Should().BeNull();

    [Theory]
    [InlineData(-1, 5, null)]
    [InlineData(10000, 5, null)]
    [InlineData(80.125, 5, null)]
    [InlineData(80, -1, null)]
    [InlineData(80, 201, null)]
    [InlineData(80, 5, 4.5)]
    [InlineData(80, 5, 10.5)]
    [InlineData(80, 5, 7.25)]
    public void Rejects_invalid_sets(decimal weight, int reps, double? rpe) =>
        SetRules.Validate(weight, reps, (decimal?)rpe).Should().NotBeNull();
}
