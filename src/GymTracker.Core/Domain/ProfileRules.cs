using System.Globalization;
using GymTracker.Core.Text;

namespace GymTracker.Core.Domain;

public static class ProfileRules
{
    /// <summary>Returns an error message, or null when the profile is within the SPEC 5.7 limits.</summary>
    public static LocalText? Validate(UserProfile p, int currentYear)
    {
        if (string.IsNullOrWhiteSpace(p.DisplayName) || p.DisplayName.Trim().Length > ProfileLimits.MaxNameLength)
        {
            return new LocalText("Name must be 1 to {0} characters.", ProfileLimits.MaxNameLength);
        }

        if ((p.Sex is { } sex && !Enum.IsDefined(sex)) || !Enum.IsDefined(p.Goal) || (p.ActivityLevel is { } level && !Enum.IsDefined(level)))
        {
            return "Pick sex, goal and activity level from the list.";
        }

        return HeightError(p.HeightCm)
            ?? BirthYearError(p.BirthYear, currentYear)
            ?? MaintenanceError(p.InitialMaintenanceKcal)
            ?? RateError(p.Goal, p.TargetRatePercentPerWeek)
            ?? ProteinError(p.ProteinGramsPerKg)
            ?? IncrementError(p.DefaultWeightIncrementKg)
            ?? TimeZoneError(p.TimeZoneId)
            ?? BarError(p.BarWeightKg)
            ?? (p.PlatesKg.Any(w => !UserProfile.PlateChoicesKg.Contains(w)) ? new LocalText("Plate weights must be from the list.") : null);
    }

    public static LocalText? HeightError(int? heightCm) =>
        heightCm is < ProfileLimits.MinHeightCm or > ProfileLimits.MaxHeightCm
            ? new LocalText("Height must be between {0} and {1} cm.", ProfileLimits.MinHeightCm, ProfileLimits.MaxHeightCm)
            : null;

    public static LocalText? BirthYearError(int? birthYear, int currentYear) =>
        birthYear is { } y && (y < ProfileLimits.MinBirthYear(currentYear) || y > ProfileLimits.MaxBirthYear(currentYear))
            ? new LocalText("Birth year must be between {0} and {1} (age {2} to {3}).",
                ProfileLimits.MinBirthYear(currentYear), ProfileLimits.MaxBirthYear(currentYear), ProfileLimits.MinAgeYears, ProfileLimits.MaxAgeYears)
            : null;

    public static LocalText? WeightError(decimal? weightKg) =>
        weightKg is { } w && (w < ProfileLimits.MinWeightKg || w > ProfileLimits.MaxWeightKg)
            ? new LocalText("Weight must be between {0} and {1} kg.", Number(ProfileLimits.MinWeightKg), Number(ProfileLimits.MaxWeightKg))
            : null;

    public static LocalText? MaintenanceError(int? kcal) =>
        kcal is < ProfileLimits.MinMaintenanceKcal or > ProfileLimits.MaxMaintenanceKcal
            ? new LocalText("Starting maintenance must be between {0} and {1} kcal.", ProfileLimits.MinMaintenanceKcal, ProfileLimits.MaxMaintenanceKcal)
            : null;

    public static LocalText? RateError(Goal goal, decimal ratePercent)
    {
        if (ratePercent < 0)
        {
            return "Rate can't be negative.";
        }

        return (goal, ProfileLimits.MaxRatePercent(goal)) switch
        {
            (Goal.Cut, { } max) when ratePercent > max => new LocalText("A cut can't be faster than {0}% of bodyweight per week.", Number(max)),
            (Goal.Bulk, { } max) when ratePercent > max => new LocalText("A bulk can't be faster than {0}% of bodyweight per week.", Number(max)),
            _ => null,
        };
    }

    public static LocalText? ProteinError(decimal gramsPerKg) =>
        gramsPerKg < ProfileLimits.MinProteinGramsPerKg || gramsPerKg > ProfileLimits.MaxProteinGramsPerKg
            ? new LocalText("Protein must be between {0} and {1} g per kg.", Number(ProfileLimits.MinProteinGramsPerKg), Number(ProfileLimits.MaxProteinGramsPerKg))
            : null;

    private static LocalText? IncrementError(decimal kg) =>
        kg < ProfileLimits.MinWeightIncrementKg || kg > ProfileLimits.MaxWeightIncrementKg
            ? new LocalText("Weight increment must be between {0} and {1} kg.", Number(ProfileLimits.MinWeightIncrementKg), Number(ProfileLimits.MaxWeightIncrementKg))
            : null;

    private static LocalText? TimeZoneError(string timeZoneId)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return null;
        }
        catch (TimeZoneNotFoundException)
        {
            return "Unknown time zone.";
        }
    }

    private static LocalText? BarError(decimal kg) =>
        kg < ProfileLimits.MinBarWeightKg || kg > ProfileLimits.MaxBarWeightKg
            ? new LocalText("Bar weight must be between {0} and {1} kg.", Number(ProfileLimits.MinBarWeightKg), Number(ProfileLimits.MaxBarWeightKg))
            : null;

    private static string Number(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
