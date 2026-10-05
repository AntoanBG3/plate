namespace GymTracker.Core.Nutrition;

/// <summary>One day's totals and how much of its fiber and saturated fat is known.</summary>
public record DaySummary(DateOnly Date, Totals Totals, DayQuality Quality);

/// <summary>Averages over the logged days of the week before today, judged against today's targets (SPEC 5.8).</summary>
/// <param name="FiberDays">Days whose fiber was known well enough to average; <see cref="FiberG"/> is over these only.</param>
/// <param name="SaturatedFatDays">The same for saturated fat.</param>
public record WeekSummary(
    int LoggedDays,
    decimal Kcal,
    decimal ProteinG,
    decimal FatG,
    decimal CarbsG,
    int ProteinMinDays,
    int FatFloorDays,
    decimal? FiberG,
    int FiberDays,
    decimal? SaturatedFatG,
    int SaturatedFatDays);

public static class WeeklyNutrition
{
    public const int Days = 7;
    public const int MinLoggedDays = 3;

    /// <summary>Null with fewer than <see cref="MinLoggedDays"/> logged days in the 7 days ending yesterday.</summary>
    public static WeekSummary? Calculate(IEnumerable<DaySummary> days, MacroTarget targets, DateOnly today)
    {
        var logged = days
            .Where(d => d.Date < today && d.Date >= today.AddDays(-Days) && d.Totals.Kcal >= AdaptiveMaintenance.MinLoggedKcal)
            .ToList();
        if (logged.Count < MinLoggedDays)
        {
            return null;
        }

        var fiber = logged.Where(d => d.Quality.FiberComplete && d.Quality.FiberG is not null).ToList();
        var saturated = logged.Where(d => d.Quality.SaturatedFatComplete && d.Quality.SaturatedFatG is not null).ToList();
        return new WeekSummary(
            logged.Count,
            R(logged.Average(d => d.Totals.Kcal)),
            R(logged.Average(d => d.Totals.ProteinG)),
            R(logged.Average(d => d.Totals.FatG)),
            R(logged.Average(d => d.Totals.CarbsG)),
            logged.Count(d => Math.Round(d.Totals.ProteinG) >= targets.ProteinMinG),
            logged.Count(d => Math.Round(d.Totals.FatG) >= targets.FatMinG),
            fiber.Count > 0 ? R(fiber.Average(d => d.Quality.FiberG!.Value)) : null,
            fiber.Count,
            saturated.Count > 0 ? R(saturated.Average(d => d.Quality.SaturatedFatG!.Value)) : null,
            saturated.Count);
    }

    private static decimal R(decimal value) => Math.Round(value, 0, MidpointRounding.AwayFromZero);
}
