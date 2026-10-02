using GymTracker.Core.Domain;

namespace GymTracker.Core.Nutrition;

/// <param name="Today">The user's current day. The window ends the day before, because today's log is incomplete.</param>
/// <param name="PriorKcal">The starting estimate (stated maintenance or Mifflin-St Jeor x 1.55), if it can be computed.</param>
/// <param name="LastAdaptiveKcal">The most recent estimate that was calculated from logged data, if any.</param>
/// <param name="WeighIns">All of the user's weigh-ins; the trend is seeded with the first one.</param>
/// <param name="IntakeByDay">Total kcal logged per day; days with nothing logged can be left out.</param>
public record MaintenanceInput(
    DateOnly Today,
    int? PriorKcal,
    int? LastAdaptiveKcal,
    IReadOnlyCollection<WeighIn> WeighIns,
    IReadOnlyDictionary<DateOnly, decimal> IntakeByDay);

/// <param name="DaysOfData">Length of the window used, at most 21.</param>
/// <param name="LoggingCompleteness">Share of window days with at least 200 kcal logged, 0 to 1.</param>
/// <param name="LowConfidence">The window has 3 or more days in a row without a weigh-in.</param>
public record MaintenanceResult(
    int TdeeKcal,
    MaintenanceMethod Method,
    int DaysOfData,
    decimal LoggingCompleteness,
    bool LowConfidence);

/// <summary>The weekly adaptive maintenance estimate (SPEC 5.5).</summary>
public static class AdaptiveMaintenance
{
    public const int WindowDays = 21;
    public const int MinWindowDays = 14;
    public const decimal MinLoggedShare = 0.8m;
    public const decimal MinWeighInShare = 0.5m;
    public const decimal MinLoggedKcal = 200m;
    public const decimal KcalPerKg = 7700m;
    public const decimal PreviousWeight = 0.7m;
    public const int MaxWeeklyChangeKcal = 100;
    public const int CadenceDays = 7;
    public const int LowConfidenceGapDays = 3;

    /// <summary>A new estimate is due when there is none yet or the latest is at least 7 days old.</summary>
    public static bool IsDue(DateOnly? lastCalculatedOn, DateOnly today) =>
        lastCalculatedOn is not { } last || today.DayNumber - last.DayNumber >= CadenceDays;

    /// <summary>
    /// Calculates the estimate for <see cref="MaintenanceInput.Today"/>. Returns null when there is neither a prior
    /// nor an earlier adaptive estimate to start from. When the window fails the data checks, the last adaptive
    /// estimate (or the prior) is carried forward with <see cref="MaintenanceMethod.Initial"/>.
    /// </summary>
    public static MaintenanceResult? Calculate(MaintenanceInput input)
    {
        if ((input.LastAdaptiveKcal ?? input.PriorKcal) is not { } previous)
        {
            return null;
        }

        var end = input.Today.AddDays(-1);
        var trend = TrendWeight.Series(input.WeighIns, end);
        if (trend.Count == 0)
        {
            return new MaintenanceResult(previous, MaintenanceMethod.Initial, 0, 0, false);
        }

        // The window can't start before the first weigh-in, because the trend doesn't exist before it.
        var window = trend.TakeLast(WindowDays).ToList();
        var days = window.Count;
        var logged = window.Where(p => input.IntakeByDay.TryGetValue(p.Date, out var kcal) && kcal >= MinLoggedKcal).ToList();
        var loggedShare = (decimal)logged.Count / days;
        var weighInShare = (decimal)window.Count(p => p.RawKg is not null) / days;
        var lowConfidence = LongestGap(window) >= LowConfidenceGapDays;

        if (days < MinWindowDays || loggedShare < MinLoggedShare || weighInShare < MinWeighInShare)
        {
            return new MaintenanceResult(previous, MaintenanceMethod.Initial, days, loggedShare, lowConfidence);
        }

        // Unlogged days are assumed to equal the average, so they drop out of the mean.
        var avgIntake = logged.Average(p => input.IntakeByDay[p.Date]);

        // The trend moves from the first to the last day of the window: that is days - 1 days of eating.
        var deltaTrend = window[^1].TrendKg - window[0].TrendKg;
        var rawTdee = avgIntake - deltaTrend * KcalPerKg / (days - 1);

        var smoothed = PreviousWeight * previous + (1 - PreviousWeight) * rawTdee;
        var clamped = Math.Clamp(smoothed, previous - MaxWeeklyChangeKcal, previous + MaxWeeklyChangeKcal);
        var tdee = (int)Math.Round(clamped, MidpointRounding.AwayFromZero);

        return new MaintenanceResult(tdee, MaintenanceMethod.Adaptive, days, loggedShare, lowConfidence);
    }

    private static int LongestGap(IEnumerable<TrendPoint> window)
    {
        int longest = 0, current = 0;
        foreach (var point in window)
        {
            current = point.RawKg is null ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }
}
