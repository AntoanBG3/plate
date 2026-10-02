namespace GymTracker.Core.Nutrition;

public record WeighIn(DateOnly Date, decimal WeightKg);

/// <summary>One day of the trend: the weigh-in if there was one, and the smoothed weight.</summary>
public record TrendPoint(DateOnly Date, decimal? RawKg, decimal TrendKg);

/// <summary>Trend weight (SPEC 5.4): an exponential moving average that ignores day-to-day water swings.</summary>
public static class TrendWeight
{
    public const decimal Alpha = 0.1m;

    /// <summary>
    /// One point per day from the first weigh-in through <paramref name="through"/>, seeded with the first weigh-in.
    /// Days without a weigh-in carry the previous trend forward unchanged. Empty when there is no weigh-in by then.
    /// </summary>
    public static List<TrendPoint> Series(IEnumerable<WeighIn> weighIns, DateOnly through)
    {
        var byDay = new Dictionary<DateOnly, decimal>();
        foreach (var w in weighIns.Where(w => w.Date <= through))
        {
            byDay[w.Date] = w.WeightKg;
        }

        if (byDay.Count == 0)
        {
            return [];
        }

        var first = byDay.Keys.Min();
        var series = new List<TrendPoint>(through.DayNumber - first.DayNumber + 1);
        var trend = byDay[first];
        for (var day = first; day <= through; day = day.AddDays(1))
        {
            decimal? raw = byDay.TryGetValue(day, out var w) ? w : null;
            if (raw is { } r)
            {
                trend += Alpha * (r - trend);
            }

            series.Add(new TrendPoint(day, raw, trend));
        }

        return series;
    }

    /// <summary>The trend on the given day, or null before the first weigh-in.</summary>
    public static decimal? On(IEnumerable<WeighIn> weighIns, DateOnly day) =>
        Series(weighIns, day) is { Count: > 0 } s ? s[^1].TrendKg : null;
}
