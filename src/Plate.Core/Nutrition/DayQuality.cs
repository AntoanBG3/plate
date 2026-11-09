using Plate.Core.Domain;

namespace Plate.Core.Nutrition;

/// <summary>
/// A day's fiber and saturated fat with how much of it is known (SPEC 5.8). Coverage is the share of the day's kcal
/// from entries that have the value; below <see cref="Complete"/> a missing value would otherwise read as zero.
/// </summary>
public record DayQuality(decimal? FiberG, decimal FiberCoverage, decimal? SaturatedFatG, decimal SaturatedFatCoverage)
{
    public const decimal Complete = 0.8m;

    public bool FiberComplete => FiberCoverage >= Complete;

    public bool SaturatedFatComplete => SaturatedFatCoverage >= Complete;

    public static DayQuality Of(IReadOnlyCollection<FoodLogEntry> entries)
    {
        var kcal = entries.Sum(e => e.Kcal);
        decimal Share(Func<FoodLogEntry, decimal?> value) =>
            kcal <= 0 ? 1m : entries.Where(e => value(e) is not null).Sum(e => e.Kcal) / kcal;
        decimal? Sum(Func<FoodLogEntry, decimal?> value) =>
            entries.Any(e => value(e) is not null) ? entries.Sum(e => value(e) ?? 0) : null;

        return new DayQuality(Sum(e => e.FiberG), Share(e => e.FiberG), Sum(e => e.SaturatedFatG), Share(e => e.SaturatedFatG));
    }
}
