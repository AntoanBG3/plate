using System.Globalization;
using GymTracker.Core.Domain;

namespace GymTracker.Core.Analytics;

/// <summary>A working set and the muscles its exercise trains.</summary>
public record VolumeSet(DateOnly Date, Muscle Primary, IReadOnlyCollection<Muscle> Secondary, bool IsWarmup = false);

public record IsoWeek(int Year, int Week)
{
    public static IsoWeek Of(DateOnly date) =>
        new(ISOWeek.GetYear(date.ToDateTime(TimeOnly.MinValue)), ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue)));

    public DateOnly Monday => DateOnly.FromDateTime(ISOWeek.ToDateTime(Year, Week, DayOfWeek.Monday));

    public IsoWeek Previous() => Of(Monday.AddDays(-7));
}

/// <summary>Working sets per muscle per ISO week (SPEC 5.3): 1 for the primary muscle, 0.5 for each secondary.</summary>
public static class WeeklyVolume
{
    public static Dictionary<IsoWeek, Dictionary<Muscle, decimal>> Calculate(IEnumerable<VolumeSet> sets)
    {
        var result = new Dictionary<IsoWeek, Dictionary<Muscle, decimal>>();
        foreach (var set in sets.Where(s => !s.IsWarmup))
        {
            var week = IsoWeek.Of(set.Date);
            if (!result.TryGetValue(week, out var muscles))
            {
                muscles = [];
                result[week] = muscles;
            }

            Add(muscles, set.Primary, 1m);
            foreach (var secondary in set.Secondary.Where(m => m != set.Primary).Distinct())
            {
                Add(muscles, secondary, 0.5m);
            }
        }

        return result;
    }

    private static void Add(Dictionary<Muscle, decimal> muscles, Muscle muscle, decimal amount) =>
        muscles[muscle] = muscles.GetValueOrDefault(muscle) + amount;
}
