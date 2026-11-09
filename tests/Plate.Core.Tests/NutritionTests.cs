using FluentAssertions;
using Plate.Core.Domain;
using Plate.Core.Nutrition;

namespace Plate.Core.Tests;

public class FoodMathTests
{
    [Fact]
    public void Scales_per_100g_values_to_the_portion()
    {
        var chicken = new Per100g(120, 22.5m, 0, 2.6m);

        FoodMath.For(chicken, 200).Should().Be(new Totals(240, 45, 0, 5.2m));
        FoodMath.For(chicken, 33).Should().Be(new Totals(39.6m, 7.4m, 0, 0.9m));
    }

    [Fact]
    public void Fiber_and_saturated_fat_scale_when_known_and_stay_unknown_otherwise()
    {
        FoodMath.For(new Per100g(350, 13, 60, 7, Fiber: 10, SaturatedFat: 1.2m), 50).Should().Be(new Totals(175, 6.5m, 30, 3.5m, 5, 0.6m));
        FoodMath.For(new Per100g(120, 22.5m, 0, 2.6m), 200).FiberG.Should().BeNull();
    }

    [Fact]
    public void Totals_add_known_fiber_and_keep_unknown_as_null()
    {
        var known = new Totals(100, 5, 10, 2, FiberG: 3, SaturatedFatG: 1);
        var unknown = new Totals(200, 10, 20, 4);

        known.Add(unknown).Should().Be(new Totals(300, 15, 30, 6, 3, 1));
        unknown.Add(unknown).FiberG.Should().BeNull();
        Totals.Zero.Add(known).FiberG.Should().Be(3);
    }

    [Fact]
    public void Rescaling_keeps_fiber_in_proportion() =>
        FoodMath.Scale(new Totals(200, 10, 30, 5, 4, null), 100, 150).Should().Be(new Totals(300, 15, 45, 7.5m, 6, null));

    [Theory]
    [InlineData(5.0, 1.0, true)]
    [InlineData(null, null, true)]
    [InlineData(-1.0, null, false)]
    [InlineData(101.0, null, false)]
    [InlineData(null, 3.4, true)]   // fat 3, within 0.5 g of rounding
    [InlineData(null, 3.6, false)]  // more saturated fat than fat
    public void Validates_fiber_and_saturated_fat(double? fiber, double? saturatedFat, bool valid) =>
        (FoodMath.Validate(100, new Per100g(100, 5, 10, 3, (decimal?)fiber, (decimal?)saturatedFat)) is null).Should().Be(valid);

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

    [Theory]
    [InlineData(650, 40, 0, 0, true)]
    [InlineData(1, 0, 0, 0, true)]
    [InlineData(5000, 500, 500, 500, true)]
    [InlineData(0, 10, 0, 0, false)]
    [InlineData(5001, 0, 0, 0, false)]
    [InlineData(500, -1, 0, 0, false)]
    [InlineData(500, 0, 501, 0, false)]
    public void Quick_entries_need_calories_and_sane_macros(decimal kcal, decimal p, decimal c, decimal f, bool ok) =>
        (FoodMath.ValidateQuick(kcal, p, c, f) is null).Should().Be(ok);

    [Fact]
    public void Scales_stored_totals_to_new_grams()
    {
        var totals = new Totals(240, 45, 0, 5.2m);
        FoodMath.Scale(totals, 200, 300).Should().Be(new Totals(360, 67.5m, 0, 7.8m));
        FoodMath.Scale(totals, 200, 200).Should().Be(totals);
    }

    [Fact]
    public void Quick_items_with_no_grams_keep_their_totals() =>
        FoodMath.Scale(new Totals(650, 40, 60, 25), 0, 150).Should().Be(new Totals(650, 40, 60, 25));
}

public class MaintenanceTests
{
    private static UserProfile Profile(Sex? sex = Sex.Male, int? height = 180, int? born = 1996, ActivityLevel? activity = null) =>
        new() { Sex = sex, HeightCm = height, BirthYear = born, ActivityLevel = activity, ProteinGramsPerKg = 2.0m };

    [Theory]
    // Male, 30 years, 180 cm, 80 kg
    [InlineData(Sex.Male, ActivityLevel.Inactive, 180, 80, 2726.17)]
    [InlineData(Sex.Male, ActivityLevel.LowActive, 180, 80, 2945.77)]
    [InlineData(Sex.Male, ActivityLevel.Active, 180, 80, 3126.32)]
    [InlineData(Sex.Male, ActivityLevel.VeryActive, 180, 80, 3495.82)]
    // Female, 30 years, 165 cm, 60 kg
    [InlineData(Sex.Female, ActivityLevel.Inactive, 165, 60, 2021.00)]
    [InlineData(Sex.Female, ActivityLevel.LowActive, 165, 60, 2182.87)]
    [InlineData(Sex.Female, ActivityLevel.Active, 165, 60, 2319.45)]
    [InlineData(Sex.Female, ActivityLevel.VeryActive, 165, 60, 2551.68)]
    public void Nasem_2023_matches_the_published_equations(Sex sex, ActivityLevel level, int height, double weight, double expected) =>
        Maintenance.Nasem2023(sex, level, 30, height, (decimal)weight).Should().Be((decimal)expected);

    [Fact]
    public void A_higher_level_never_gives_less_where_the_equations_cross()
    {
        // Male, 30 years, 150 cm, 45 kg: the very active equation alone gives 2358.67, below active's 2373.87.
        Maintenance.Nasem2023(Sex.Male, ActivityLevel.VeryActive, 30, 150, 45).Should().Be(2373.87m);
        Maintenance.Nasem2023(Sex.Male, ActivityLevel.Active, 30, 150, 45).Should().Be(2373.87m);
    }

    [Fact]
    public void Levels_are_in_order_across_the_profile_limits()
    {
        foreach (var sex in new[] { Sex.Male, Sex.Female })
        foreach (var age in new[] { ProfileLimits.MinAgeYears, 40, 70, ProfileLimits.MaxAgeYears })
        foreach (var height in new[] { ProfileLimits.MinHeightCm, 160, 190, ProfileLimits.MaxHeightCm })
        foreach (var weight in new[] { ProfileLimits.MinWeightKg, 60m, 120m, ProfileLimits.MaxWeightKg })
        {
            var values = Enum.GetValues<ActivityLevel>().Select(l => Maintenance.Nasem2023(sex, l, age, height, weight)).ToList();
            values.Should().BeInAscendingOrder($"{sex}, {age} y, {height} cm, {weight} kg");
        }
    }

    [Fact]
    public void Prior_uses_the_activity_level_and_age_from_birth_year()
    {
        Maintenance.Prior(Profile(activity: ActivityLevel.LowActive), 80, currentYear: 2026).Should().Be(2946);
        Maintenance.Prior(Profile(activity: ActivityLevel.VeryActive), 80, currentYear: 2026).Should().Be(3496);
    }

    [Fact]
    public void Prior_without_an_activity_level_is_inactive() =>
        Maintenance.Prior(Profile(activity: null), 80, 2026).Should().Be(2726);

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
        MacroTargets.ProteinAim(Profile(), weight, new DateOnly(2026, 10, 5)).Should().Be(expected);
}

public class ProfileRulesTests
{
    private static UserProfile Valid() => new() { DisplayName = "Antoan", HeightCm = 180, BirthYear = 1996, Goal = Goal.Maintain };

    [Theory]
    [InlineData(Goal.Cut, null, true)]
    [InlineData(Goal.Bulk, null, true)]
    [InlineData(Goal.Cut, 0.5, false)]
    [InlineData(Goal.Cut, 0.0, false)]
    [InlineData(Goal.Maintain, null, false)]
    [InlineData(null, null, false)]
    public void A_cut_or_bulk_needs_a_rate(Goal? goal, double? rate, bool refused) =>
        (ProfileRules.MissingRateError(goal, (decimal?)rate) is not null).Should().Be(refused);

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
    [InlineData(1926, true)]    // 100 in 2026
    [InlineData(1925, false)]
    [InlineData(2008, true)]    // 18
    [InlineData(2009, false)]   // 17: the adult equations and floors don't apply
    public void Age_must_be_18_to_100(int born, bool valid)
    {
        var p = Valid();
        p.BirthYear = born;
        (ProfileRules.Validate(p, 2026) is null).Should().Be(valid);
    }

    [Theory]
    [InlineData(120, true)]
    [InlineData(119, false)]
    [InlineData(230, true)]
    [InlineData(231, false)]
    public void Height_must_be_120_to_230_cm(int height, bool valid)
    {
        var p = Valid();
        p.HeightCm = height;
        (ProfileRules.Validate(p, 2026) is null).Should().Be(valid);
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(29.9, false)]
    [InlineData(300, true)]
    [InlineData(300.1, false)]
    public void Bodyweight_must_be_30_to_300_kg(double weight, bool valid) =>
        (ProfileRules.WeightError((decimal)weight) is null).Should().Be(valid);

    [Theory]
    [InlineData(1200, true)]
    [InlineData(1199, false)]
    [InlineData(6000, true)]
    [InlineData(6001, false)]
    public void Starting_maintenance_must_be_1200_to_6000_kcal(int kcal, bool valid)
    {
        var p = Valid();
        p.InitialMaintenanceKcal = kcal;
        (ProfileRules.Validate(p, 2026) is null).Should().Be(valid);
    }

    [Theory]
    [InlineData(0.8, true)]
    [InlineData(0.7, false)]
    [InlineData(3.5, true)]
    [InlineData(3.6, false)]
    public void Protein_must_be_0_8_to_3_5_g_per_kg(double protein, bool valid)
    {
        var p = Valid();
        p.ProteinGramsPerKg = (decimal)protein;
        (ProfileRules.Validate(p, 2026) is null).Should().Be(valid);
    }

    [Theory]
    [InlineData(0.25, true)]
    [InlineData(0.2, false)]
    [InlineData(20, true)]
    [InlineData(20.5, false)]
    public void Weight_increment_must_be_0_25_to_20_kg(double increment, bool valid)
    {
        var p = Valid();
        p.DefaultWeightIncrementKg = (decimal)increment;
        (ProfileRules.Validate(p, 2026) is null).Should().Be(valid);
    }

    [Theory]
    [InlineData("A", true)]
    [InlineData("  Antoan  ", true)]   // measured after trimming
    [InlineData("   ", false)]
    [InlineData("", false)]
    public void Name_must_not_be_blank(string name, bool valid)
    {
        var p = Valid();
        p.DisplayName = name;
        (ProfileRules.Validate(p, 2026) is null).Should().Be(valid);
    }

    [Fact]
    public void Name_is_at_most_50_characters()
    {
        var p = Valid();
        p.DisplayName = new string('a', 50);
        ProfileRules.Validate(p, 2026).Should().BeNull();
        p.DisplayName = new string('a', 51);
        ProfileRules.Validate(p, 2026).Should().NotBeNull();
    }

    [Fact]
    public void Enum_values_outside_the_list_are_refused()
    {
        var p = Valid();
        p.Sex = (Sex)7;
        ProfileRules.Validate(p, 2026).Should().NotBeNull();

        p = Valid();
        p.Goal = (Goal)7;
        ProfileRules.Validate(p, 2026).Should().NotBeNull();

        p = Valid();
        p.ActivityLevel = (ActivityLevel)7;
        ProfileRules.Validate(p, 2026).Should().NotBeNull();

        p = Valid();
        p.ActivityLevel = ActivityLevel.VeryActive;
        ProfileRules.Validate(p, 2026).Should().BeNull();
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
