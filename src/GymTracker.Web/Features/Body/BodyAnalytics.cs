using GymTracker.Core.Nutrition;
using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Body;

public record WeightChart(string Range, IReadOnlyList<TrendPoint> Points);

/// <param name="Kcal">Logged kcal, or null when nothing was logged that day.</param>
/// <param name="TargetKcal">The target from the estimate in force that day.</param>
public record CalorieDay(DateOnly Date, decimal? Kcal, int? TargetKcal, int? MaintenanceKcal);

/// <summary>Bodyweight and calorie history for the Progress charts.</summary>
public class BodyAnalytics(AppDbContext db, BodyweightService bodyweights)
{
    public const string DefaultRange = "12w";
    public static readonly IReadOnlyList<(string Key, string Label)> Ranges = [("4w", "4 weeks"), ("12w", "12 weeks"), ("all", "All")];
    public const int CalorieDays = 28;

    /// <summary>Weigh-ins and the trend line, one point per day, for the last 4 or 12 weeks or everything.</summary>
    public async Task<WeightChart> WeightAsync(string? range, DateOnly today, CancellationToken ct)
    {
        range = Ranges.Any(r => r.Key == range) ? range! : DefaultRange;
        var weighIns = await bodyweights.WeighInsAsync(ct);

        // The trend is always computed from the first weigh-in, then cut to the range.
        var from = range switch
        {
            "4w" => today.AddDays(-27),
            "12w" => today.AddDays(-83),
            _ => DateOnly.MinValue,
        };
        return new WeightChart(range, TrendWeight.Series(weighIns, today).Where(p => p.Date >= from).ToList());
    }

    /// <summary>Daily intake for the last 4 weeks with the target and maintenance in force each day.</summary>
    public async Task<List<CalorieDay>> CaloriesAsync(DateOnly today, CancellationToken ct)
    {
        var from = today.AddDays(1 - CalorieDays);
        var intake = await db.FoodLog
            .Where(e => e.Date >= from && e.Date <= today)
            .GroupBy(e => e.Date)
            .Select(g => new { Date = g.Key, Kcal = g.Sum(e => e.Kcal) })
            .ToDictionaryAsync(x => x.Date, x => x.Kcal, ct);
        var estimates = await db.MaintenanceEstimates
            .Where(e => e.CalculatedOn <= today)
            .OrderBy(e => e.CalculatedOn)
            .Select(e => new { e.CalculatedOn, e.TargetKcal, e.EstimatedTdeeKcal })
            .ToListAsync(ct);

        var days = new List<CalorieDay>(CalorieDays);
        for (var day = from; day <= today; day = day.AddDays(1))
        {
            var estimate = estimates.LastOrDefault(e => e.CalculatedOn <= day);
            days.Add(new CalorieDay(
                day,
                intake.TryGetValue(day, out var kcal) ? decimal.Round(kcal) : null,
                estimate?.TargetKcal,
                estimate?.EstimatedTdeeKcal));
        }

        return days;
    }
}
