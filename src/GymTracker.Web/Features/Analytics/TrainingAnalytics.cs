using GymTracker.Core.Analytics;
using GymTracker.Core.Domain;
using GymTracker.Core.Training;
using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Analytics;

public record PersonalRecord(string ExerciseName, decimal E1rmKg, decimal? PreviousBestKg);

public record E1rmPoint(DateOnly Date, decimal E1rmKg);

public record ExerciseOption(int Id, string Name);

public record VolumeComparison(IsoWeek ThisWeek, IReadOnlyList<(Muscle Muscle, decimal ThisWeek, decimal LastWeek)> Muscles);

/// <summary>Reads training history for progression suggestions, records and charts. The math lives in Core.</summary>
public class TrainingAnalytics(AppDbContext db, TimeProvider time)
{
    /// <summary>A suggestion per exercise in the session, keyed by session exercise id; null where there is no history.</summary>
    public async Task<Dictionary<int, ProgressionSuggestion?>> SuggestionsAsync(WorkoutSession session, CancellationToken ct)
    {
        var result = new Dictionary<int, ProgressionSuggestion?>();
        foreach (var exercise in session.Exercises)
        {
            var history = await RecentSessionSetsAsync(exercise.ExerciseId, session, 2, ct);
            var rules = new ProgressionRules(
                exercise.RepMin,
                exercise.RepMax,
                exercise.WorkingSets,
                exercise.WeightIncrementKg,
                exercise.Exercise.Equipment == Equipment.Bodyweight);
            result[exercise.Id] = Progression.Suggest(rules, history);
        }

        return result;
    }

    /// <summary>The session's best e1RM per exercise that beats every earlier session's.</summary>
    public async Task<List<PersonalRecord>> RecordsAsync(WorkoutSession session, CancellationToken ct)
    {
        var exerciseIds = session.Exercises.Select(e => e.ExerciseId).Distinct().ToList();
        var earlier = await db.Sets
            .Where(s => !s.IsWarmup
                && exerciseIds.Contains(s.SessionExercise.ExerciseId)
                && s.SessionExercise.SessionId != session.Id
                && s.SessionExercise.Session.StartedAt < session.StartedAt)
            .Select(s => new { s.SessionExercise.ExerciseId, s.WeightKg, s.Reps })
            .ToListAsync(ct);

        var records = new List<PersonalRecord>();
        foreach (var group in session.Exercises.GroupBy(e => e.ExerciseId))
        {
            var best = OneRepMax.Best(group.SelectMany(e => e.Sets).Select(ToLogged));
            if (best is null)
            {
                continue;
            }

            var previous = earlier
                .Where(s => s.ExerciseId == group.Key)
                .Select(s => OneRepMax.Estimate(s.WeightKg, s.Reps))
                .Max();

            // The first time an exercise is done is not a record, just a baseline.
            if (previous is not null && best > previous)
            {
                records.Add(new PersonalRecord(group.First().Exercise.Name, Round(best.Value), Round(previous.Value)));
            }
        }

        return records;
    }

    public async Task<List<ExerciseOption>> ExercisesWithHistoryAsync(CancellationToken ct) =>
        (await db.Sets
            .Where(s => !s.IsWarmup)
            .Select(s => new { s.SessionExercise.ExerciseId, s.SessionExercise.Exercise.Name })
            .Distinct()
            .ToListAsync(ct))
        .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
        .Select(e => new ExerciseOption(e.ExerciseId, e.Name))
        .ToList();

    /// <summary>Best e1RM per session for one exercise, oldest first.</summary>
    public async Task<List<E1rmPoint>> E1rmSeriesAsync(int exerciseId, CancellationToken ct)
    {
        var zone = await ZoneAsync(ct);
        var rows = await db.Sets
            .Where(s => !s.IsWarmup && s.SessionExercise.ExerciseId == exerciseId)
            .Select(s => new { s.SessionExercise.SessionId, s.SessionExercise.Session.StartedAt, s.WeightKg, s.Reps })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => new { r.SessionId, r.StartedAt })
            .Select(g => new
            {
                g.Key.StartedAt,
                Best = OneRepMax.Best(g.Select(r => new LoggedSet(r.WeightKg, r.Reps))),
            })
            .Where(x => x.Best is not null)
            .OrderBy(x => x.StartedAt)
            .Select(x => new E1rmPoint(LocalDate(x.StartedAt, zone), Round(x.Best!.Value)))
            .ToList();
    }

    /// <summary>Working sets per muscle for this ISO week and the one before, in the user's time zone.</summary>
    public async Task<VolumeComparison> VolumeAsync(CancellationToken ct)
    {
        var zone = await ZoneAsync(ct);
        var thisWeek = IsoWeek.Of(LocalDate(time.GetUtcNow(), zone));
        var lastWeek = thisWeek.Previous();
        var since = new DateTimeOffset(lastWeek.Monday.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var rows = await db.Sets
            .Where(s => !s.IsWarmup && s.CompletedAt >= since)
            .Select(s => new { s.CompletedAt, s.SessionExercise.Exercise.PrimaryMuscle, s.SessionExercise.Exercise.SecondaryMuscles })
            .ToListAsync(ct);

        var weeks = WeeklyVolume.Calculate(rows.Select(r => new VolumeSet(LocalDate(r.CompletedAt, zone), r.PrimaryMuscle, r.SecondaryMuscles)));
        var current = weeks.GetValueOrDefault(thisWeek) ?? [];
        var previous = weeks.GetValueOrDefault(lastWeek) ?? [];

        var muscles = Enum.GetValues<Muscle>()
            .Select(m => (m, current.GetValueOrDefault(m), previous.GetValueOrDefault(m)))
            .ToList();
        return new VolumeComparison(thisWeek, muscles);
    }

    /// <summary>Working sets from the most recent earlier sessions that included this exercise, newest first.</summary>
    private async Task<List<IReadOnlyList<LoggedSet>>> RecentSessionSetsAsync(int exerciseId, WorkoutSession current, int sessions, CancellationToken ct)
    {
        // Sessions with only warmups for this exercise are skipped by Core, so fetch a few extra.
        var sessionIds = await db.SessionExercises
            .Where(e => e.ExerciseId == exerciseId && e.SessionId != current.Id && e.Session.StartedAt < current.StartedAt)
            .OrderByDescending(e => e.Session.StartedAt)
            .Select(e => e.SessionId)
            .Take(sessions * 3)
            .ToListAsync(ct);
        var recent = sessionIds.Distinct().Take(sessions + 2).ToList();

        var sets = await db.Sets
            .Where(s => s.SessionExercise.ExerciseId == exerciseId && recent.Contains(s.SessionExercise.SessionId))
            .Select(s => new { s.SessionExercise.SessionId, s.SetNumber, s.CompletedAt, s.WeightKg, s.Reps, s.IsWarmup })
            .ToListAsync(ct);

        return recent
            .Select(id => (IReadOnlyList<LoggedSet>)sets
                .Where(s => s.SessionId == id)
                .OrderBy(s => s.CompletedAt).ThenBy(s => s.SetNumber)
                .Select(s => new LoggedSet(s.WeightKg, s.Reps, s.IsWarmup))
                .ToList())
            .ToList();
    }

    private async Task<TimeZoneInfo> ZoneAsync(CancellationToken ct) =>
        TimeZoneInfo.FindSystemTimeZoneById(
            await db.Profiles.Select(p => p.TimeZoneId).SingleOrDefaultAsync(ct) ?? UserProfile.DefaultTimeZoneId);

    private static DateOnly LocalDate(DateTimeOffset utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime);

    private static LoggedSet ToLogged(SetEntry s) => new(s.WeightKg, s.Reps, s.IsWarmup);

    private static decimal Round(decimal value) => Math.Round(value, 1);
}
