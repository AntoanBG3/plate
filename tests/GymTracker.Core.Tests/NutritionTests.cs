using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;

namespace GymTracker.Core.Tests;

public class FoodMathTests
{
    [Fact]
    public void Scales_per_100g_values_to_the_portion()
    {
        var chicken = new Per100g(120, 22.5m, 0, 2.6m);

        FoodMath.For(chicken, 200).Should().Be(new Totals(240, 45, 0, 5.2m));
        FoodMath.For(chicken, 33).Should().Be(new Totals(39.6m, 7.4m, 0, 0.9m));
    }

    [Theory]
    [InlineData(120, 22.5, 0, 2.6, false)]    // chicken: 113.4 from macros, within 15%
    [InlineData(130, 2.7, 28, 0.3, false)]    // cooked rice
    [InlineData(300, 10, 10, 1, true)]        // 89 kcal from macros
    [InlineData(0, 5, 5, 0, true)]
    [InlineData(0, 0, 0, 0, false)]           // water
    public void Flags_calories_that_disagree_with_macros(decimal kcal, decimal p, decimal c, decimal f, bool disagree) =>
        FoodMath.MacrosDisagree(new Per100g(kcal, p, c, f)).Should().Be(disagree);

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(3001)]
    public void Rejects_impossible_portions(decimal grams) =>
        FoodMath.Validate(grams, new Per100g(100, 10, 10, 1)).Should().NotBeNull();

    [Fact]
    public void Rejects_negative_or_impossible_per_100g_values()
    {
        FoodMath.Validate(100, new Per100g(-1, 10, 10, 1)).Should().NotBeNull();
        FoodMath.Validate(100, new Per100g(950, 0, 0, 100)).Should().NotBeNull();
        FoodMath.Validate(100, new Per100g(400, 60, 60, 0)).Should().NotBeNull();
        FoodMath.Validate(3000, new Per100g(884, 0, 0, 100)).Should().BeNull("pure oil is real");
    }
}

public class MaintenanceTests
{
    private static UserProfile Profile(Sex? sex = Sex.Male, int? height = 180, int? born = 1996) =>
        new() { Sex = sex, HeightCm = height, BirthYear = born, ProteinGramsPerKg = 2.0m };

    [Fact]
    public void Mifflin_st_jeor_matches_the_published_formula()
    {
        // Male, 80 kg, 180 cm, 30 years: 800 + 1125 - 150 + 5 = 1780
        Maintenance.MifflinStJeor(Sex.Male, 80, 180, 30).Should().Be(1780m);
        // Female, 60 kg, 165 cm, 55 years: 600 + 1031.25 - 275 - 161 = 1195.25
        Maintenance.MifflinStJeor(Sex.Female, 60, 165, 55).Should().Be(1195.25m);
    }

    [Fact]
    public void Prior_is_bmr_times_1_55_using_age_from_birth_year() =>
        Maintenance.Prior(Profile(), 80, currentYear: 2026).Should().Be(2759); // 1780 * 1.55 = 2759

    [Fact]
    public void A_stated_maintenance_wins() =>
        Maintenance.Prior(new UserProfile { InitialMaintenanceKcal = 2500 }, null, 2026).Should().Be(2500);

    [Fact]
    public void Prior_is_unknown_without_the_inputs()
    {
        Maintenance.Prior(Profile(), null, 2026).Should().BeNull();
        Maintenance.Prior(Profile(sex: null), 80, 2026).Should().BeNull();
        Maintenance.Prior(Profile(height: null), 80, 2026).Should().BeNull();
        Maintenance.Prior(Profile(born: null), 80, 2026).Should().BeNull();
    }

    [Theory]
    [InlineData(82.1, 165)]   // 164.2 → 165
    [InlineData(80, 160)]
    [InlineData(61.2, 120)]   // 122.4 → 120
    public void Protein_target_rounds_to_5_grams(decimal weight, int expected) =>
        Maintenance.ProteinTarget(Profile(), weight).Should().Be(expected);
}

public class ProfileRulesTests
{
    private static UserProfile Valid() => new() { DisplayName = "Antoan", HeightCm = 180, BirthYear = 1996, Goal = Goal.Maintain };

    [Fact]
    public void A_normal_profile_is_valid() => ProfileRules.Validate(Valid(), 2026).Should().BeNull();

    [Theory]
    [InlineData(Goal.Cut, 1.0, true)]
    [InlineData(Goal.Cut, 1.1, false)]
    [InlineData(Goal.Bulk, 0.5, true)]
    [InlineData(Goal.Bulk, 0.6, false)]
    [InlineData(Goal.Maintain, 3.0, true)]   // ignored for maintain
    public void Caps_the_weekly_rate(Goal goal, double rate, bool valid)
    {
        var p = Valid();
        p.Goal = goal;
        p.TargetRatePercentPerWeek = (decimal)rate;

        (ProfileRules.Validate(p, 2026) is null).Should().Be(valid);
    }

    [Fact]
    public void Rejects_out_of_range_values()
    {
        var p = Valid();
        p.HeightCm = 90;
        ProfileRules.Validate(p, 2026).Should().NotBeNull();

        p = Valid();
        p.TimeZoneId = "Mars/Olympus";
        ProfileRules.Validate(p, 2026).Should().NotBeNull();

        p = Valid();
        p.ProteinGramsPerKg = 5;
        ProfileRules.Validate(p, 2026).Should().NotBeNull();
    }

    [Theory]
    [InlineData(-0.5, false)]
    [InlineData(0, true)]
    [InlineData(50, true)]
    [InlineData(50.5, false)]
    public void Bar_weight_must_be_0_to_50_kg(double bar, bool valid)
    {
        var p = Valid();
        p.BarWeightKg = (decimal)bar;
        (ProfileRules.Validate(p, 2026) is null).Should().Be(valid);
    }

    [Fact]
    public void Plates_must_come_from_the_list()
    {
        var p = Valid();
        p.PlatesKg = [20m, 0.5m];
        ProfileRules.Validate(p, 2026).Should().BeNull();

        p.PlatesKg = [20m, 7m];
        ProfileRules.Validate(p, 2026).Should().NotBeNull();
    }
}

public class BarcodeTests
{
    [Theory]
    [InlineData("4006381333931", true)]   // EAN-13
    [InlineData("5449000000996", true)]   // EAN-13
    [InlineData("96385074", true)]        // EAN-8
    [InlineData("036000291452", true)]    // UPC-A
    [InlineData("4006381333932", false)]  // wrong check digit
    [InlineData("12345", false)]
    [InlineData("abcdefghijklm", false)]
    [InlineData(null, false)]
    public void Checks_length_digits_and_check_digit(string? code, bool valid) =>
        Barcodes.IsValid(code).Should().Be(valid);
}
