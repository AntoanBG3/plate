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

    private static readonly decimal[] Standard = [25m, 20m, 15m, 10m, 5m, 2.5m, 1.25m];

    [Fact]
    public void Loads_the_fewest_plates_heaviest_first()
    {
        var load = PlateMath.PerSide(100m, 20m, Standard);
        load.PerSide.Should().Equal(25m, 15m);
        (load.TotalKg, load.Exact).Should().Be((100m, true));
    }

    [Fact]
    public void Small_plates_make_up_the_rest() =>
        PlateMath.PerSide(82.5m, 20m, Standard).PerSide.Should().Equal(25m, 5m, 1.25m);

    [Fact]
    public void Searches_every_combination_instead_of_taking_the_heaviest_plate() =>
        PlateMath.PerSide(60m, 20m, [15m, 10m]).PerSide.Should().Equal(10m, 10m);

    [Fact]
    public void An_unreachable_weight_gives_the_closest_lower_load()
    {
        var load = PlateMath.PerSide(83m, 20m, Standard);
        (load.TotalKg, load.Exact).Should().Be((82.5m, false));
    }

    [Fact]
    public void The_bar_alone_is_exact()
    {
        var load = PlateMath.PerSide(20m, 20m, Standard);
        (load.PerSide.Count, load.TotalKg, load.Exact, load.BelowBar).Should().Be((0, 20m, true, false));
    }

    [Fact]
    public void Less_than_the_bar_is_flagged()
    {
        var load = PlateMath.PerSide(15m, 20m, Standard);
        (load.PerSide.Count, load.TotalKg, load.Exact, load.BelowBar).Should().Be((0, 20m, false, true));
    }

    [Fact]
    public void No_plates_means_only_the_bar()
    {
        var load = PlateMath.PerSide(60m, 20m, []);
        (load.PerSide.Count, load.TotalKg, load.Exact).Should().Be((0, 20m, false));
    }

    [Fact]
    public void Heavy_loads_work() =>
        PlateMath.PerSide(300m, 20m, Standard).PerSide.Should().Equal(25m, 25m, 25m, 25m, 25m, 15m);

    [Fact]
    public void Ignores_plates_that_are_not_positive_multiples_of_50_grams() =>
        PlateMath.PerSide(22.5m, 20m, [1.25m, 0m, -5m, 1.13m]).PerSide.Should().Equal(1.25m);

    [Fact]
    public void An_absurd_target_is_capped_at_the_heaviest_loggable_weight()
    {
        var load = PlateMath.PerSide(10_000_000_000m, 20m, Standard);
        load.TotalKg.Should().BeLessThanOrEqualTo(SetRules.MaxWeightKg);
        load.Exact.Should().BeFalse();
    }

    [Fact]
    public void A_zero_kilo_bar_works_for_plate_loaded_machines() =>
        PlateMath.PerSide(90m, 0m, Standard).PerSide.Should().Equal(25m, 20m);
}
