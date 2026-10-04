using GymTracker.Core.Text;

namespace GymTracker.Core.Domain;

public static class ProfileRules
{
    public const decimal MaxCutRatePercent = 1.0m;
    public const decimal MaxBulkRatePercent = 0.5m;

    /// <summary>Returns an error message, or null when the profile is valid (including the SPEC 5.6 safety limits).</summary>
    public static LocalText? Validate(UserProfile p, int currentYear)
    {
        if (string.IsNullOrWhiteSpace(p.DisplayName) || p.DisplayName.Length > 100)
        {
            return "Name must be 1 to 100 characters.";
        }

        if (p.HeightCm is < 120 or > 230)
        {
            return "Height must be between 120 and 230 cm.";
        }

        if (p.BirthYear is { } y && (y < currentYear - 100 || y > currentYear - 13))
        {
            return "Birth year is out of range.";
        }

        if (p.InitialMaintenanceKcal is < 1200 or > 6000)
        {
            return "Starting maintenance must be between 1200 and 6000 kcal.";
        }

        if (p.TargetRatePercentPerWeek < 0)
        {
            return "Rate can't be negative.";
        }

        if (p.Goal == Goal.Cut && p.TargetRatePercentPerWeek > MaxCutRatePercent)
        {
            return new LocalText("A cut can't be faster than {0}% of bodyweight per week.", MaxCutRatePercent.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (p.Goal == Goal.Bulk && p.TargetRatePercentPerWeek > MaxBulkRatePercent)
        {
            return new LocalText("A bulk can't be faster than {0}% of bodyweight per week.", MaxBulkRatePercent.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (p.ProteinGramsPerKg is < 0.8m or > 3.5m)
        {
            return "Protein must be between 0.8 and 3.5 g per kg.";
        }

        if (p.DefaultWeightIncrementKg is <= 0 or > 20)
        {
            return "Weight increment must be above 0 and at most 20 kg.";
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(p.TimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return "Unknown time zone.";
        }

        if (p.BarWeightKg is < 0 or > 50)
        {
            return "Bar weight must be between 0 and 50 kg.";
        }

        if (p.PlatesKg.Any(w => !UserProfile.PlateChoicesKg.Contains(w)))
        {
            return "Plate weights must be from the list.";
        }

        return null;
    }
}
