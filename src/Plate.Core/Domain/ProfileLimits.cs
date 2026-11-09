namespace Plate.Core.Domain;

/// <summary>
/// Every limit on profile values (SPEC 5.7). Validation, the weigh-in, the Settings inputs and the setup chat all read
/// these, so a value one screen accepts is never refused by another.
/// </summary>
public static class ProfileLimits
{
    public const int MaxNameLength = 50;

    /// <summary>The energy equations, calorie floors and rate caps are adult guidance.</summary>
    public const int MinAgeYears = 18;
    public const int MaxAgeYears = 100;

    public const int MinHeightCm = 120;
    public const int MaxHeightCm = 230;

    public const decimal MinWeightKg = 30m;
    public const decimal MaxWeightKg = 300m;

    public const int MinMaintenanceKcal = 1200;
    public const int MaxMaintenanceKcal = 6000;

    public const decimal MaxCutRatePercent = 1.0m;
    public const decimal MaxBulkRatePercent = 0.5m;

    public const decimal MinProteinGramsPerKg = 0.8m;
    public const decimal MaxProteinGramsPerKg = 3.5m;

    public const decimal MinWeightIncrementKg = 0.25m;
    public const decimal MaxWeightIncrementKg = 20m;

    public const decimal MinBarWeightKg = 0m;
    public const decimal MaxBarWeightKg = 50m;

    public static int MinBirthYear(int currentYear) => currentYear - MaxAgeYears;

    public static int MaxBirthYear(int currentYear) => currentYear - MinAgeYears;

    /// <summary>The highest weekly rate allowed for a goal; Maintain ignores the rate.</summary>
    public static decimal? MaxRatePercent(Goal goal) => goal switch
    {
        Goal.Cut => MaxCutRatePercent,
        Goal.Bulk => MaxBulkRatePercent,
        _ => null,
    };
}
