using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Training;

namespace GymTracker.Core.Tests;

public class PlateMathTests
{
    [Fact]
    public void An_exercise_bar_weight_wins_over_the_profile() =>
        PlateMath.BarFor(Equipment.Barbell, 10m, 20m).Should().Be(10m);

    [Fact]
    public void Barbell_exercises_use_the_profile_bar_by_default() =>
        PlateMath.BarFor(Equipment.Barbell, null, 20m).Should().Be(20m);

    [Fact]
    public void Other_equipment_has_no_bar_unless_one_is_set()
    {
        PlateMath.BarFor(Equipment.Machine, null, 20m).Should().BeNull();
        PlateMath.BarFor(Equipment.Machine, 0m, 20m).Should().Be(0m, "a plate-loaded machine opts in with 0");
    }
}
