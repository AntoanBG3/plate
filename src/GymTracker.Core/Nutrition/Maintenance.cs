using GymTracker.Core.Domain;

namespace GymTracker.Core.Nutrition;

/// <summary>The starting maintenance estimate before there is enough logged data (SPEC 5.5 step 1).</summary>
public static class Maintenance
{
    private readonly record struct Equation(decimal Intercept, decimal PerYear, decimal PerCm, decimal PerKg);

    // NASEM Dietary Reference Intakes for Energy (2023), adults: estimated energy requirement in kcal per day,
    // fitted to doubly labeled water measurements. Indexed by ActivityLevel.
    private static readonly Equation[] Men =
    [
        new(753.07m, -10.83m, 6.50m, 14.10m),
        new(581.47m, -10.83m, 8.30m, 14.94m),
        new(1004.82m, -10.83m, 6.52m, 15.91m),
        new(-517.88m, -10.83m, 15.61m, 19.11m),
    ];

    private static readonly Equation[] Women =
    [
        new(584.90m, -7.01m, 5.72m, 11.71m),
        new(575.77m, -7.01m, 6.60m, 12.14m),
        new(710.25m, -7.01m, 6.54m, 12.34m),
        new(511.83m, -7.01m, 9.07m, 12.56m),
    ];

    /// <summary>
    /// NASEM 2023 total energy expenditure in kcal per day. The equations cross for very small bodies, so this is the
    /// highest value among <paramref name="level"/> and every lower level: more activity never lowers the estimate.
    /// </summary>
    public static decimal Nasem2023(Sex sex, ActivityLevel level, int ageYears, int heightCm, decimal weightKg)
    {
        var equations = sex == Sex.Male ? Men : Women;
        return equations[..((int)level + 1)].Max(e => e.Intercept + e.PerYear * ageYears + e.PerCm * heightCm + e.PerKg * weightKg);
    }

    /// <summary>
    /// The user's stated maintenance if set, otherwise the NASEM 2023 estimate (no activity level counts as inactive).
    /// Null when neither is possible because the profile lacks sex, height, birth year or there is no weigh-in yet.
    /// </summary>
    public static int? Prior(UserProfile profile, decimal? weightKg, int currentYear)
    {
        if (profile.InitialMaintenanceKcal is { } stated)
        {
            return stated;
        }

        if (profile.Sex is not { } sex || profile.HeightCm is not { } height || profile.BirthYear is not { } born || weightKg is not { } weight)
        {
            return null;
        }

        var tee = Nasem2023(sex, profile.ActivityLevel ?? ActivityLevel.Inactive, currentYear - born, height, weight);
        return (int)Math.Round(tee, MidpointRounding.AwayFromZero);
    }

    /// <summary>Daily protein target, rounded to 5 g (SPEC 5.6).</summary>
    public static int? ProteinTarget(UserProfile profile, decimal? weightKg) =>
        weightKg is { } w ? (int)(Math.Round(w * profile.ProteinGramsPerKg / 5m, MidpointRounding.AwayFromZero) * 5) : null;
}
