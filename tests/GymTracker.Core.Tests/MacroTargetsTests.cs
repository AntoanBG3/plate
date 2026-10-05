using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;

namespace GymTracker.Core.Tests;

public class MacroTargetsTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);

    private static UserProfile Profile(
        Goal goal = Goal.Maintain, decimal rate = 0, int? height = 180, int? born = 1996, decimal? proteinPerKg = null) =>
        new() { Goal = goal, TargetRatePercentPerWeek = rate, HeightCm = height, BirthYear = born, Sex = Sex.Male, ProteinGramsPerKg = proteinPerKg };

    [Fact]
    public void A_cut_at_80_kg_gets_every_target_from_the_spec_example()
    {
        // 30-year-old man, 180 cm, 80 kg, cutting 0.5% a week: 2,060 kcal (SPEC 5.6 test).
        var m = MacroTargets.Calculate(2060, Profile(Goal.Cut, 0.5m), 80m, Today)!;

        m.ReferenceKg.Should().Be(80m);
        m.ProteinMinG.Should().Be(130);      // 1.6 * 80 = 128
        m.ProteinAimG.Should().Be(175);      // 2.2 * 80 = 176
        m.FatMinG.Should().Be(45);           // 20% of 2060 / 9 = 45.8
        m.FatMaxG.Should().Be(80);           // 35% of 2060 / 9 = 80.1
        m.CarbsMinG.Should().Be(160);        // (2060 - 700 - 720) / 4
        m.CarbsMaxG.Should().Be(240);        // (2060 - 700 - 405) / 4 = 238.75
        m.FiberMinG.Should().Be(29);         // 14 * 2.06 = 28.8
        m.SaturatedFatMaxG.Should().Be(23);  // 10% of 2060 / 9 = 22.9
        m.MealProteinG.Should().Be(25);      // 0.3 * 80 = 24
        m.ProteinLowered.Should().BeFalse();
    }

    [Fact]
    public void Maintenance_at_80_kg_aims_lower_on_protein_and_scales_the_rest_with_calories()
    {
        var m = MacroTargets.Calculate(2500, Profile(), 80m, Today)!;

        m.ProteinMinG.Should().Be(130);
        m.ProteinAimG.Should().Be(145);      // 1.8 * 80 = 144
        m.FatMinG.Should().Be(55);           // 55.6
        m.FatMaxG.Should().Be(95);           // 97.2
        m.CarbsMinG.Should().Be(265);        // (2500 - 580 - 855) / 4 = 266.25
        m.CarbsMaxG.Should().Be(355);        // (2500 - 580 - 495) / 4 = 356.25
        m.FiberMinG.Should().Be(35);
        m.SaturatedFatMaxG.Should().Be(28);  // 27.8
    }

    [Theory]
    [InlineData(Goal.Maintain, 0, 1.8)]
    [InlineData(Goal.Bulk, 0.25, 1.8)]
    [InlineData(Goal.Cut, 0.5, 2.2)]
    [InlineData(Goal.Cut, 0.75, 2.2)]
    [InlineData(Goal.Cut, 0.76, 2.4)]
    [InlineData(Goal.Cut, 1.0, 2.4)]
    public void Auto_protein_depends_on_goal_and_cut_rate(Goal goal, double rate, double perKg) =>
        MacroTargets.AutoProteinPerKg(goal, (decimal)rate).Should().Be((decimal)perKg);

    [Fact]
    public void A_custom_protein_setting_replaces_auto() =>
        MacroTargets.Calculate(2500, Profile(proteinPerKg: 2.5m), 80m, Today)!.ProteinAimG.Should().Be(200);

    [Fact]
    public void The_aim_is_never_below_the_minimum() =>
        MacroTargets.Calculate(2500, Profile(proteinPerKg: 1.0m), 80m, Today)!.ProteinAimG.Should().Be(130);

    [Fact]
    public void A_high_bmi_bases_protein_on_the_weight_at_bmi_27()
    {
        // 110 kg at 175 cm is BMI 35.9; 27 * 1.75² = 82.7 kg.
        var m = MacroTargets.Calculate(2400, Profile(Goal.Cut, 0.5m, height: 175), 110m, Today)!;

        m.ReferenceKg.Should().BeApproximately(82.69m, 0.01m);
        m.ProteinAimG.Should().Be(180, "2.2 g/kg of 82.7 kg, not of 110 kg (240)");
    }

    [Theory]
    [InlineData(87.48, 87.48)]  // 180 cm at exactly BMI 27
    [InlineData(87.6, 87.48)]   // just over
    [InlineData(80, 80)]
    public void The_reference_weight_caps_at_bmi_27(double trend, double expected) =>
        MacroTargets.ReferenceWeight((decimal)trend, 180).Should().BeApproximately((decimal)expected, 0.001m);

    [Fact]
    public void Without_a_height_the_trend_weight_is_used() =>
        MacroTargets.ReferenceWeight(120m, null).Should().Be(120m);

    [Fact]
    public void Without_a_trend_weight_there_are_no_macro_targets() =>
        MacroTargets.Calculate(2500, Profile(), null, Today).Should().BeNull();

    [Fact]
    public void From_65_the_protein_minimum_is_lower_and_meals_need_more()
    {
        // A 70-year-old at 80 kg: 1.2 * 80 = 96; per meal 0.4 * 80 = 32.
        var m = MacroTargets.Calculate(2200, Profile(born: 1956), 80m, Today)!;

        m.ProteinMinG.Should().Be(95);
        m.MealProteinG.Should().Be(30);
    }

    [Theory]
    [InlineData(1962, 130)] // 64
    [InlineData(1961, 95)]  // 65
    [InlineData(null, 130)] // unknown age counts as under 65
    public void The_age_rule_starts_at_65(int? born, int min) =>
        MacroTargets.Calculate(2500, Profile(born: born), 80m, Today)!.ProteinMinG.Should().Be(min);

    [Fact]
    public void Older_adults_get_at_least_25_g_per_meal() =>
        // 0.4 * 50 = 20, raised to 25.
        MacroTargets.Calculate(1800, Profile(born: 1950), 50m, Today)!.MealProteinG.Should().Be(25);

    [Fact]
    public void The_fat_floor_is_half_a_gram_per_kg_when_that_is_more_than_20_percent()
    {
        // 20% of 1500 / 9 = 33.3; 0.5 * 87 = 43.5.
        MacroTargets.Calculate(1500, Profile(), 87m, Today)!.FatMinG.Should().Be(45);
    }

    [Fact]
    public void Fiber_is_at_least_25_g() =>
        MacroTargets.Calculate(1500, Profile(), 60m, Today)!.FiberMinG.Should().Be(25);

    [Fact]
    public void Protein_is_lowered_when_protein_and_fat_would_fill_more_than_the_calories()
    {
        // 90 kg at 185 cm (BMI 26.3), custom 3.5 g/kg = 315 g, on a 1,500 kcal floor. Fat floor 0.5 * 90 = 45 g.
        // 1500 - 405 = 1095 kcal for protein: 273.75 g, so 270.
        var m = MacroTargets.Calculate(1500, Profile(height: 185, proteinPerKg: 3.5m), 90m, Today)!;

        m.ProteinAimG.Should().Be(270);
        m.ProteinLowered.Should().BeTrue();
        m.CarbsMinG.Should().Be(0);
        m.CarbsMaxG.Should().Be(5);  // (1500 - 1080 - 405) / 4 = 3.75
    }

    [Fact]
    public void Lowering_never_goes_below_the_protein_minimum()
    {
        // 87 kg (no height), 1.6 * 87 = 139 → 140 g minimum; 3.5 g/kg on 700 kcal leaves no room at all.
        var m = MacroTargets.Calculate(700, Profile(height: null, proteinPerKg: 3.5m), 87m, Today)!;

        m.ProteinAimG.Should().Be(140);
        m.ProteinLowered.Should().BeTrue();
        m.CarbsMaxG.Should().Be(0);
    }
}
