using GymTracker.Core.Analytics;
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Core.Summary;
using GymTracker.Core.Time;
using GymTracker.Web.Data;
using GymTracker.Web.Features.Analytics;
using GymTracker.Web.Features.Body;
using GymTracker.Web.Features.Food;
using GymTracker.Web.Features.Profile;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Summary;

/// <summary>What Today shows for the weekly summary: the stored one, or whether one can be written now.</summary>
public record WeeklySummaryState(WeeklySummary? Stored, bool CanWrite);

/// <summary>
/// Last week's AI summary (SPEC 6.5): gathers the week's numbers, has the model write it once, and stores it for the
/// rest of the week. Today reads the stored one; writing happens from a separate request so the page never waits.
/// </summary>
public class WeeklySummaryService(
    AppDbContext db,
    ICurrentUser currentUser,
    ProfileService profiles,
    FoodLogService food,
    NutritionTargets targets,
    BodyweightService bodyweights,
    TrainingAnalytics analytics,
    IWeeklySummaryWriter writer,
    TimeProvider time,
    ILogger<WeeklySummaryService> log)
{
    public const int TopMuscles = 3;

    public async Task<WeeklySummaryState> StateAsync(CancellationToken ct)
    {
        var profile = await profiles.GetAsync(ct);
        if (!profile.WeeklySummaryEnabled)
        {
            return new WeeklySummaryState(null, false);
        }

        var monday = await LastMondayAsync(ct);
        var stored = await db.WeeklySummaries.AsNoTracking().SingleOrDefaultAsync(s => s.WeekStart == monday, ct);
        if (stored is not null && stored.Language == profile.Language)
        {
            return new WeeklySummaryState(stored, false);
        }

        var canWrite = await writer.IsAvailableAsync(ct) && (await FactsAsync(monday, ct)).HasEnough;
        return new WeeklySummaryState(null, canWrite);
    }

    /// <summary>Writes and stores last week's summary if there is none in the account's language; null when it can't.</summary>
    public async Task<WeeklySummary?> WriteAsync(CancellationToken ct)
    {
        var state = await StateAsync(ct);
        if (state.Stored is not null || !state.CanWrite)
        {
            return state.Stored;
        }

        var profile = await profiles.GetAsync(ct);
        var monday = await LastMondayAsync(ct);
        var result = await writer.WriteAsync(await FactsAsync(monday, ct), profile.Language == Language.Bulgarian, ct);
        if (result.Text is not { } text)
        {
            log.LogWarning("Weekly summary not written: {Error}", result.Error?.Format);
            return null;
        }

        var row = await db.WeeklySummaries.SingleOrDefaultAsync(s => s.WeekStart == monday, ct);
        if (row is null)
        {
            row = new WeeklySummary { UserId = currentUser.UserId ?? throw new InvalidOperationException("No signed-in user."), WeekStart = monday };
            db.WeeklySummaries.Add(row);
        }

        (row.Summary, row.Tips, row.Motivation) = (text.Summary, [.. text.Tips], text.Motivation);
        (row.Language, row.Model, row.CreatedAt) = (profile.Language, result.Model, time.GetUtcNow());
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two visits raced and the other one stored this week's summary first.
            db.Entry(row).State = EntityState.Detached;
            return await db.WeeklySummaries.AsNoTracking().SingleAsync(s => s.WeekStart == monday, ct);
        }

        return row;
    }

    /// <summary>The numbers sent to the model for the week starting <paramref name="monday"/>.</summary>
    public async Task<WeekFacts> FactsAsync(DateOnly monday, CancellationToken ct)
    {
        var profile = await profiles.GetAsync(ct);
        var until = monday.AddDays(7);
        var macros = (await targets.ForTodayAsync(ct)).Macros;
        var foodWeek = macros is null ? null : WeeklyNutrition.Between(await food.DaysBetweenAsync(monday, until, ct), macros, monday, until);

        var weighIns = await bodyweights.WeighInsAsync(ct);
        var inWeek = weighIns.Count(w => w.Date >= monday && w.Date < until);
        var trendEnd = TrendWeight.On(weighIns, monday.AddDays(6));
        var trendStart = TrendWeight.On(weighIns, monday.AddDays(-1)) ?? TrendWeight.On(weighIns, monday);
        (decimal, decimal)? trend = trendStart is { } s && trendEnd is { } e ? (decimal.Round(s, 1), decimal.Round(e, 1)) : null;

        var zone = TimeZoneInfo.FindSystemTimeZoneById(profile.TimeZoneId);
        var since = new DateTimeOffset(monday.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var before = new DateTimeOffset(until.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var sessions = (await db.Sessions.AsNoTracking()
                .Include(x => x.Exercises).ThenInclude(x => x.Sets)
                .Include(x => x.Exercises).ThenInclude(x => x.Exercise)
                .Where(x => x.EndedAt != null && x.StartedAt >= since && x.StartedAt < before)
                .ToListAsync(ct))
            .Where(x => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.StartedAt, zone).DateTime) is var d && d >= monday && d < until)
            .ToList();

        var records = new List<string>();
        foreach (var session in sessions)
        {
            records.AddRange((await analytics.RecordsAsync(session, ct)).Select(r => $"{r.ExerciseName}: {r.E1rmKg:0.#} kg (was {r.PreviousBestKg:0.#})"));
        }

        var working = sessions.SelectMany(x => x.Exercises).SelectMany(x => x.Sets.Where(set => !set.IsWarmup).Select(_ => x.Exercise)).ToList();
        var muscles = WeeklyVolume.Calculate(working.Select(x => new VolumeSet(monday, x.PrimaryMuscle, x.SecondaryMuscles)))
            .Values.SelectMany(v => v)
            .OrderByDescending(m => m.Value)
            .Take(TopMuscles)
            .Select(m => m.Key.ToString())
            .ToList();

        return new WeekFacts(
            monday,
            profile.Goal,
            profile.TargetRatePercentPerWeek,
            profile.DietBreakUntil is { } breakUntil && breakUntil >= monday,
            foodWeek,
            macros,
            trend,
            inWeek,
            sessions.Count,
            working.Count,
            records,
            muscles);
    }

    private async Task<DateOnly> LastMondayAsync(CancellationToken ct) => IsoWeek.Of(await profiles.TodayAsync(ct)).Previous().Monday;
}
