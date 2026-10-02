using GymTracker.Core.Domain;

namespace GymTracker.Core.Nutrition;

/// <summary>The starting maintenance estimate before there is enough logged data (SPEC 5.5 step 1).</summary>
public static class Maintenance
{
    public const decimal ActivityFactor = 1.55m;

    /// <summary>Mifflin-St Jeor resting energy, in kcal per day.</summary>
    public static decimal MifflinStJeor(Sex sex, decimal weightKg, int heightCm, int ageYears) =>
        10 * weightKg + 6.25m * heightCm - 5 * ageYears + (sex == Sex.Male ? 5 : -161);

    /// <summary>
    /// The user's stated maintenance if set, otherwise Mifflin-St Jeor × 1.55. Null when neither is possible
    /// because the profile lacks sex, height, birth year or there is no weigh-in yet.
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

        var bmr = MifflinStJeor(sex, weight, height, currentYear - born);
        return (int)Math.Round(bmr * ActivityFactor, MidpointRounding.AwayFromZero);
    }

    /// <summary>Daily protein target, rounded to 5 g (SPEC 5.6).</summary>
    public static int? ProteinTarget(UserProfile profile, decimal? weightKg) =>
        weightKg is { } w ? (int)(Math.Round(w * profile.ProteinGramsPerKg / 5m, MidpointRounding.AwayFromZero) * 5) : null;
}
