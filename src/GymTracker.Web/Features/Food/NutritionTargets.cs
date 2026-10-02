using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Core.Time;
using GymTracker.Web.Data;
using GymTracker.Web.Features.Profile;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Food;

/// <summary>Today's targets and the maintenance estimate behind them.</summary>
/// <param name="Missing">What the user still needs to enter before a target can be shown.</param>
/// <param name="FloorKcal">The minimum target for the user's sex, shown when <paramref name="FloorApplied"/>.</param>
public record DailyTargets(
    int? Kcal,
    int? ProteinG,
    string? Missing,
    MaintenanceEstimate? Estimate = null,
    decimal? TrendWeightKg = null,
    bool FloorApplied = false,
    int FloorKcal = 0);

/// <summary>
/// Keeps the weekly maintenance estimate (SPEC 5.5) and turns it into today's targets (SPEC 5.6).
/// The math is in Core; this class reads the inputs and stores the result.
/// </summary>
public class NutritionTargets(AppDbContext db, ICurrentUser currentUser, ProfileService profiles, TimeProvider time)
{
    public async Task<DailyTargets> ForTodayAsync(CancellationToken ct)
    {
        var profile = await profiles.GetAsync(ct);
        var today = UserClock.Today(time.GetUtcNow(), profile.TimeZoneId);
        var weighIns = await db.Bodyweights
            .OrderBy(b => b.Date)
            .Select(b => new WeighIn(b.Date, b.WeightKg))
            .ToListAsync(ct);
        var trend = TrendWeight.On(weighIns, today);

        var estimate = await CurrentEstimateAsync(profile, today, weighIns, trend, ct);
        if (estimate is null)
        {
            var missing = trend is null
                ? "Log your weight and fill in your profile to get a calorie target."
                : "Fill in sex, height and birth year (or a starting maintenance) in Settings to get a calorie target.";
            return new DailyTargets(null, Maintenance.ProteinTarget(profile, trend), missing, TrendWeightKg: trend);
        }

        var target = GoalTargets.Calculate(estimate.EstimatedTdeeKcal, profile, trend);

        // Keep the stored targets in line with profile changes made since the estimate, so the calories chart matches.
        if (estimate.TargetKcal != target.Kcal || estimate.TargetProteinG != target.ProteinG)
        {
            estimate.TargetKcal = target.Kcal;
            estimate.TargetProteinG = target.ProteinG;
            await db.SaveChangesAsync(ct);
        }

        return new DailyTargets(
            target.Kcal,
            target.ProteinG,
            target.Kcal is null ? "Log your weight to get a target for your goal."
                : target.ProteinG is null ? "Log your weight to get a protein target." : null,
            estimate,
            trend,
            target.FloorApplied,
            GoalTargets.MinKcal(profile.Sex));
    }

    /// <summary>The latest estimate if it is less than a week old, otherwise a new one, stored. Null when no estimate is possible.</summary>
    private async Task<MaintenanceEstimate?> CurrentEstimateAsync(
        UserProfile profile, DateOnly today, List<WeighIn> weighIns, decimal? trend, CancellationToken ct)
    {
        var prior = Maintenance.Prior(profile, trend, today.Year);
        var latest = await db.MaintenanceEstimates.OrderByDescending(e => e.CalculatedOn).FirstOrDefaultAsync(ct);
        var lastAdaptive = await db.MaintenanceEstimates
            .Where(e => e.Method == MaintenanceMethod.Adaptive)
            .OrderByDescending(e => e.CalculatedOn)
            .Select(e => (int?)e.EstimatedTdeeKcal)
            .FirstOrDefaultAsync(ct);

        if (latest is not null && !AdaptiveMaintenance.IsDue(latest.CalculatedOn, today))
        {
            // Until the first adaptive estimate, the stored value is only the prior: follow profile edits right away.
            if (lastAdaptive is null && prior is { } p && latest.EstimatedTdeeKcal != p)
            {
                latest.EstimatedTdeeKcal = p;
                await db.SaveChangesAsync(ct);
            }

            return latest;
        }

        var from = today.AddDays(-AdaptiveMaintenance.WindowDays);
        var intake = await db.FoodLog
            .Where(e => e.Date >= from && e.Date < today)
            .GroupBy(e => e.Date)
            .Select(g => new { Date = g.Key, Kcal = g.Sum(e => e.Kcal) })
            .ToDictionaryAsync(x => x.Date, x => x.Kcal, ct);

        var result = AdaptiveMaintenance.Calculate(new MaintenanceInput(today, prior, lastAdaptive, weighIns, intake));
        if (result is null)
        {
            return null;
        }

        var target = GoalTargets.Calculate(result.TdeeKcal, profile, trend);
        var estimate = new MaintenanceEstimate
        {
            UserId = currentUser.UserId ?? throw new InvalidOperationException("No signed-in user."),
            CalculatedOn = today,
            EstimatedTdeeKcal = result.TdeeKcal,
            TargetKcal = target.Kcal,
            TargetProteinG = target.ProteinG,
            TrendWeightKg = trend is { } t ? decimal.Round(t, 2) : null,
            DaysOfData = result.DaysOfData,
            LoggingCompleteness = decimal.Round(result.LoggingCompleteness, 3),
            Method = result.Method,
            LowConfidence = result.LowConfidence,
        };
        db.MaintenanceEstimates.Add(estimate);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two page loads raced on the same day and the other one stored today's estimate first.
            db.Entry(estimate).State = EntityState.Detached;
            return await db.MaintenanceEstimates.SingleAsync(e => e.CalculatedOn == today, ct);
        }

        return estimate;
    }
}
