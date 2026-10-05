using GymTracker.Core.Domain;

namespace GymTracker.Core.Nutrition;

/// <summary>Daily macro targets in grams (SPEC 5.6). Protein has a minimum and an aim; fat and carbs are ranges.</summary>
/// <param name="ReferenceKg">The weight protein and the fat floor are based on: trend weight, capped at BMI 27.</param>
/// <param name="MealProteinG">The protein each of breakfast, lunch and dinner aims for.</param>
/// <param name="ProteinLowered">The aim was lowered because protein and fat would have filled the whole calorie target.</param>
public record MacroTarget(
    decimal ReferenceKg,
    int ProteinMinG,
    int ProteinAimG,
    int FatMinG,
    int FatMaxG,
    int CarbsMinG,
    int CarbsMaxG,
    int FiberMinG,
    int SaturatedFatMaxG,
    int MealProteinG,
    bool ProteinLowered);

public static class MacroTargets
{
    /// <summary>Above this BMI, grams per kg of total weight overshoots, so protein uses the weight at this BMI.</summary>
    public const decimal ReferenceBmi = 27m;

    /// <summary>From this age the protein minimum follows the older-adult evidence and meals need more protein.</summary>
    public const int OlderAdultAge = 65;

    /// <summary>A cut faster than this (% of bodyweight a week) gets the higher auto protein aim.</summary>
    public const decimal FastCutRatePercent = 0.75m;

    public static decimal ReferenceWeight(decimal trendKg, int? heightCm)
    {
        if (heightCm is not { } cm)
        {
            return trendKg;
        }

        var metres = cm / 100m;
        return Math.Min(trendKg, ReferenceBmi * metres * metres);
    }

    /// <summary>The protein aim in g per kg when the user has not set one.</summary>
    public static decimal AutoProteinPerKg(Goal goal, decimal ratePercentPerWeek) => goal switch
    {
        Goal.Cut when ratePercentPerWeek > FastCutRatePercent => 2.4m,
        Goal.Cut => 2.2m,
        _ => 1.8m,
    };

    /// <summary>The protein aim before it is fitted to calories; used when there is no calorie target yet.</summary>
    public static int? ProteinAim(UserProfile profile, decimal? trendKg, DateOnly today)
    {
        if (trendKg is not { } trend)
        {
            return null;
        }

        var refKg = ReferenceWeight(trend, profile.HeightCm);
        return Math.Max(Round5(MinProteinPerKg(profile, today) * refKg), Round5(AimPerKg(profile) * refKg));
    }

    /// <summary>Null without a trend weight, since every target scales with it.</summary>
    public static MacroTarget? Calculate(int kcal, UserProfile profile, decimal? trendKg, DateOnly today)
    {
        if (trendKg is not { } trend)
        {
            return null;
        }

        var refKg = ReferenceWeight(trend, profile.HeightCm);
        var older = IsOlderAdult(profile, today);

        var proteinMin = Round5(MinProteinPerKg(profile, today) * refKg);
        var proteinAim = Math.Max(proteinMin, Round5(AimPerKg(profile) * refKg));

        var fatMin = Round5(Math.Max(0.20m * kcal / 9m, 0.5m * refKg));
        var fatMax = Math.Max(fatMin, Round5(0.35m * kcal / 9m));

        // Protein and the fat floor must fit in the calories; lower the aim in 5 g steps, never below the minimum.
        var lowered = false;
        if (4 * proteinAim + 9 * fatMin > kcal)
        {
            var room = (kcal - 9m * fatMin) / 4m;
            proteinAim = Math.Max(proteinMin, (int)Math.Floor(room / 5m) * 5);
            lowered = true;
        }

        var carbsMin = Math.Max(0, Round5((kcal - 4m * proteinAim - 9m * fatMax) / 4m));
        var carbsMax = Math.Max(0, Round5((kcal - 4m * proteinAim - 9m * fatMin) / 4m));

        var fiber = (int)Math.Round(Math.Max(25m, 14m * kcal / 1000m), MidpointRounding.AwayFromZero);
        var saturatedFat = (int)Math.Round(0.10m * kcal / 9m, MidpointRounding.AwayFromZero);
        var meal = older ? Round5(Math.Max(0.4m * refKg, 25m)) : Round5(0.3m * refKg);

        return new MacroTarget(refKg, proteinMin, proteinAim, fatMin, fatMax, carbsMin, carbsMax, fiber, saturatedFat, meal, lowered);
    }

    private static bool IsOlderAdult(UserProfile profile, DateOnly today) =>
        profile.BirthYear is { } born && today.Year - born >= OlderAdultAge;

    private static decimal MinProteinPerKg(UserProfile profile, DateOnly today) => IsOlderAdult(profile, today) ? 1.2m : 1.6m;

    private static decimal AimPerKg(UserProfile profile) =>
        profile.ProteinGramsPerKg ?? AutoProteinPerKg(profile.Goal, profile.TargetRatePercentPerWeek);

    private static int Round5(decimal grams) => (int)(Math.Round(grams / 5m, MidpointRounding.AwayFromZero) * 5);
}
