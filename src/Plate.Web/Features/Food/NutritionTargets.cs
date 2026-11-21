using Plate.Core.Domain;
using Plate.Core.Nutrition;
using Plate.Core.Time;
using Plate.Web.Data;
using Plate.Web.Features.Body;
using Plate.Web.Features.Profile;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Features.Food;

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
    int FloorKcal = 0,
    MacroTarget? Macros = null);

/// <summary>
/// Keeps the weekly maintenance estimate (SPEC 5.5) and turns it into today's targets (SPEC 5.6).
/// The math is in Core; this class reads the inputs and stores the result.
/// </summary>
public class NutritionTargets(AppDbContext db, ICurrentUser currentUser, ProfileService profiles, BodyweightService bodyweights, TimeProvider time)
{
    public async Task<DailyTargets> ForTodayAsync(CancellationToken ct)
    {
        var profile = await profiles.GetAsync(ct);
        var today = UserClock.Today(time.GetUtcNow(), profile.TimeZoneId);
        var weighIns = await bodyweights.WeighInsAsync(ct);
        var trend = TrendWeight.On(weighIns, today);

        var estimate = await CurrentEstimateAsync(profile, today, weighIns, trend, ct);
        if (estimate is null)
        {
            var missing = trend is null
                ? "Log your weight and fill in your profile to get a calorie target."
                : "Fill in sex, height and birth year (or a starting maintenance) in Settings to get a calorie target.";
            return new DailyTargets(null, MacroTargets.ProteinAim(profile, trend, today), missing, TrendWeightKg: trend);
        }

        var target = GoalTargets.Calculate(estimate.EstimatedTdeeKcal, profile, trend, today);

        // A weekly estimate is historical once its day ends. Today's target changes get their own snapshot.
        if (estimate.CalculatedOn == today && (estimate.TargetKcal != target.Kcal || estimate.TargetProteinG != target.ProteinG))
        {
            estimate.TargetKcal = target.Kcal;
            estimate.TargetProteinG = target.ProteinG;
            await db.SaveChangesAsync(ct);
        }

        var userId = currentUser.UserId ?? throw new InvalidOperationException("No signed-in user.");
        var savedValues = new TargetDayValues(estimate.EstimatedTdeeKcal, target, trend, GoalTargets.MinKcal(profile.Sex));
        var stored = await db.NutritionTargetDays.AsNoTracking().Where(x => x.Date == today).Select(x => x.Values).FirstOrDefaultAsync(ct);
        if (stored is null)
        {
            await CaptureEarlierDaysAsync(profile, weighIns, today, ct);
        }

        // Most page loads change nothing; only a change is written. Concurrent page loads share one row, and only
        // today's row is ever updated, never a previous day.
        if (stored is null || TargetDayValues.Read(stored) != savedValues)
        {
            var values = savedValues.ToJson();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "NutritionTargetDays" ("UserId", "Date", "Values") VALUES ({userId}, {today}, {values}::jsonb)
                ON CONFLICT ("UserId", "Date") DO UPDATE SET "Values" = EXCLUDED."Values"
                """, ct);
        }

        return new DailyTargets(
            target.Kcal,
            target.ProteinG,
            target.Kcal is null ? "Log your weight to get a target for your goal."
                : target.ProteinG is null ? "Log your weight to get a protein target." : null,
            WithTargetValues(estimate, savedValues),
            trend,
            target.FloorApplied,
            GoalTargets.MinKcal(profile.Sex),
            target.Macros);
    }

    /// <summary>
    /// The last targets captured on or before a past day (SPEC 4, NutritionTargetDay). Days from before snapshots
    /// existed are captured the first time this user's targets are (<see cref="TargetDayValues.Legacy"/>).
    /// Null before the first estimate.
    /// </summary>
    public async Task<(DailyTargets? Targets, DateOnly? FirstEstimate)> ForDateAsync(DateOnly date, CancellationToken ct)
    {
        var estimate = await db.MaintenanceEstimates.Where(e => e.CalculatedOn <= date).OrderByDescending(e => e.CalculatedOn).FirstOrDefaultAsync(ct);
        if (estimate is null)
        {
            var first = await db.MaintenanceEstimates.OrderBy(e => e.CalculatedOn).Select(e => (DateOnly?)e.CalculatedOn).FirstOrDefaultAsync(ct);
            return (null, first);
        }

        var snapshot = await SnapshotOnAsync(date, ct);
        if (snapshot is null)
        {
            // Opened straight from a link before anything captured this user's targets.
            var profile = await profiles.GetAsync(ct);
            var weighIns = await bodyweights.WeighInsAsync(ct);
            await CaptureEarlierDaysAsync(profile, weighIns, UserClock.Today(time.GetUtcNow(), profile.TimeZoneId), ct);
            snapshot = await SnapshotOnAsync(date, ct);
        }

        // An initial weekly estimate can still follow profile edits; return the maintenance actually shown then.
        var saved = snapshot is null ? TargetDayValues.Legacy(estimate, await profiles.GetAsync(ct), estimate.TrendWeightKg, date) : TargetDayValues.Read(snapshot.Values);
        estimate = WithTargetValues(estimate, saved);
        return (new DailyTargets(saved.Target.Kcal, saved.Target.ProteinG, null, estimate, saved.TrendWeightKg,
            saved.Target.FloorApplied, saved.FloorKcal, saved.Target.Macros), estimate.CalculatedOn);
    }

    /// <summary>Makes sure the days before snapshots existed are captured, before a profile edit could change them.</summary>
    public async Task CaptureHistoryAsync(CancellationToken ct)
    {
        var profile = await profiles.GetAsync(ct);
        var weighIns = await bodyweights.WeighInsAsync(ct);
        await CaptureEarlierDaysAsync(profile, weighIns, UserClock.Today(time.GetUtcNow(), profile.TimeZoneId), ct);
    }

    private Task<NutritionTargetDay?> SnapshotOnAsync(DateOnly date, CancellationToken ct) =>
        db.NutritionTargetDays.AsNoTracking().Where(x => x.Date <= date).OrderByDescending(x => x.Date).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Once per user, when nothing has been captured yet: freezes every day from the first estimate to yesterday as it
    /// was shown before snapshots existed, so a later profile edit no longer changes them.
    /// </summary>
    private async Task CaptureEarlierDaysAsync(UserProfile profile, IReadOnlyList<WeighIn> weighIns, DateOnly today, CancellationToken ct)
    {
        if (await db.NutritionTargetDays.AnyAsync(ct))
        {
            return;
        }

        var estimates = await db.MaintenanceEstimates.AsNoTracking().Where(e => e.CalculatedOn < today).OrderBy(e => e.CalculatedOn).ToListAsync(ct);
        if (estimates.Count == 0)
        {
            return;
        }

        var userId = currentUser.UserId ?? throw new InvalidOperationException("No signed-in user.");
        var trends = TrendWeight.Series(weighIns, today.AddDays(-1));
        var estimateIndex = 0;
        var rows = new List<NutritionTargetDay>();
        for (var day = estimates[0].CalculatedOn; day < today; day = day.AddDays(1))
        {
            if (estimateIndex + 1 < estimates.Count && estimates[estimateIndex + 1].CalculatedOn == day)
            {
                estimateIndex++;
            }
            decimal? trend = trends.Count == 0 || day < trends[0].Date ? null : trends[day.DayNumber - trends[0].Date.DayNumber].TrendKg;
            var values = TargetDayValues.Legacy(estimates[estimateIndex], profile, trend, day);
            rows.Add(new NutritionTargetDay { UserId = userId, Date = day, Values = values.ToJson() });
        }

        db.NutritionTargetDays.AddRange(rows);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent page load captured them first; its rows are the same.
            rows.ForEach(r => db.Entry(r).State = EntityState.Detached);
        }
    }

    private static MaintenanceEstimate WithTargetValues(MaintenanceEstimate estimate, TargetDayValues values) => new()
    {
        Id = estimate.Id, UserId = estimate.UserId, CalculatedOn = estimate.CalculatedOn,
        EstimatedTdeeKcal = values.MaintenanceKcal, TargetKcal = values.Target.Kcal, TargetProteinG = values.Target.ProteinG,
        TrendWeightKg = values.TrendWeightKg, DaysOfData = estimate.DaysOfData, LoggingCompleteness = estimate.LoggingCompleteness,
        Method = estimate.Method, LowConfidence = estimate.LowConfidence,
    };

    /// <summary>The latest estimate if it is less than a week old, otherwise a new one, stored. Null when no estimate is possible.</summary>
    private async Task<MaintenanceEstimate?> CurrentEstimateAsync(
        UserProfile profile, DateOnly today, IReadOnlyList<WeighIn> weighIns, decimal? trend, CancellationToken ct)
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
                if (latest.CalculatedOn == today)
                {
                    latest.EstimatedTdeeKcal = p;
                    await db.SaveChangesAsync(ct);
                }
                else
                {
                    // Use the new prior today without changing the estimate older days relied on or its cadence.
                    return WithTargetValues(latest, new TargetDayValues(p,
                        GoalTargets.Calculate(p, profile, trend, today), trend, GoalTargets.MinKcal(profile.Sex)));
                }
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

        var target = GoalTargets.Calculate(result.TdeeKcal, profile, trend, today);
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
