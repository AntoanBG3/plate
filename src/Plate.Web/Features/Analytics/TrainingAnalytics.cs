using Plate.Core.Analytics;
using Plate.Core.Domain;
using Plate.Core.Training;
using Plate.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Features.Analytics;

public record PersonalRecord(string ExerciseName, decimal E1rmKg, decimal? PreviousBestKg, bool IsCustom = false);

public record E1rmPoint(DateOnly Date, decimal E1rmKg);

public record ExerciseOption(int Id, string Name, bool IsCustom = false);

public record VolumeComparison(IsoWeek ThisWeek, IReadOnlyList<(Muscle Muscle, decimal ThisWeek, decimal LastWeek)> Muscles);

/// <summary>Reads training history for progression suggestions, records and charts. The math lives in Core.</summary>
public class TrainingAnalytics(AppDbContext db, TimeProvider time)
{
    public async Task<(Dictionary<int, ProgressionSuggestion?> Suggestions, Dictionary<int, IReadOnlyList<LoggedSet>> LastTime)>
        GuidanceAsync(WorkoutSession session, CancellationToken ct)
    {
        var histories = await RecentHistoryAsync(session.Exercises.Select(e => e.ExerciseId).Distinct().ToArray(), session, ct);
        var suggestions = new Dictionary<int, ProgressionSuggestion?>();
        var lastTime = new Dictionary<int, IReadOnlyList<LoggedSet>>();
        foreach (var exercise in session.Exercises)
        {
            var history = histories.GetValueOrDefault(exercise.ExerciseId) ?? [];
            suggestions[exercise.Id] = Progression.Suggest(Rules(exercise), history);
            if (history.Count > 0)
            {
                lastTime[exercise.Id] = history[0].Where(s => !s.IsWarmup).ToList();
            }
        }
        return (suggestions, lastTime);
    }

    private static ProgressionRules Rules(SessionExercise exercise) =>
        new(exercise.RepMin, exercise.RepMax, exercise.WorkingSets, exercise.WeightIncrementKg,
            exercise.Exercise.Equipment == Equipment.Bodyweight);

    /// <summary>
    /// The inputs the progression rules received for one session exercise, and what they suggested; a feedback
    /// report keeps them so the case can be replayed in a Core test (SPEC 11).
    /// </summary>
    public async Task<(SessionExercise Exercise, ProgressionRules Rules, List<IReadOnlyList<LoggedSet>> History, ProgressionSuggestion? Suggestion)> SuggestionInputsAsync(
        int sessionExerciseId, CancellationToken ct)
    {
        var exercise = await db.SessionExercises
            .Include(e => e.Session)
            .Include(e => e.Exercise)
            .FirstOrDefaultAsync(e => e.Id == sessionExerciseId, ct) ?? throw new NotFoundException("Exercise not found.");
        var histories = await RecentHistoryAsync([exercise.ExerciseId], exercise.Session, ct);
        var history = histories.GetValueOrDefault(exercise.ExerciseId) ?? [];
        var rules = Rules(exercise);
        return (exercise, rules, history, Progression.Suggest(rules, history));
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
                records.Add(new PersonalRecord(group.First().Exercise.Name, Round(best.Value), Round(previous.Value), group.First().Exercise.OwnerUserId is not null));
            }
        }

        return records;
    }

    /// <summary>
    /// Exercises done for the first time in this session, with their best e1RM: a baseline, shown apart from records.
    /// </summary>
    public async Task<List<PersonalRecord>> FirstTimesAsync(WorkoutSession session, CancellationToken ct)
    {
        var exerciseIds = session.Exercises.Select(e => e.ExerciseId).Distinct().ToList();
        var done = (await db.Sets
            .Where(s => !s.IsWarmup
                && exerciseIds.Contains(s.SessionExercise.ExerciseId)
                && s.SessionExercise.SessionId != session.Id
                && s.SessionExercise.Session.StartedAt < session.StartedAt)
            .Select(s => s.SessionExercise.ExerciseId)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();

        return session.Exercises
            .GroupBy(e => e.ExerciseId)
            .Where(g => !done.Contains(g.Key))
            .Select(g => (Exercise: g.First().Exercise, Best: OneRepMax.Best(g.SelectMany(e => e.Sets).Select(ToLogged))))
            .Where(x => x.Best is not null)
            .Select(x => new PersonalRecord(x.Exercise.Name, Round(x.Best!.Value), null, x.Exercise.OwnerUserId is not null))
            .ToList();
    }

    public async Task<List<ExerciseOption>> ExercisesWithHistoryAsync(CancellationToken ct) =>
        (await db.Sets
            .Where(s => !s.IsWarmup)
            .Select(s => new { s.SessionExercise.ExerciseId, s.SessionExercise.Exercise.Name, IsCustom = s.SessionExercise.Exercise.OwnerUserId != null })
            .Distinct()
            .ToListAsync(ct))
        .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
        .Select(e => new ExerciseOption(e.ExerciseId, e.Name, e.IsCustom))
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

    private async Task<Dictionary<int, List<IReadOnlyList<LoggedSet>>>> RecentHistoryAsync(int[] exerciseIds, WorkoutSession current, CancellationToken ct)
    {
        var rows = await (
            from exercise in db.Exercises.Where(e => exerciseIds.Contains(e.Id))
            from previous in db.Sessions.Where(s => s.StartedAt < current.StartedAt &&
                s.Exercises.Any(e => e.ExerciseId == exercise.Id && e.Sets.Any(set => !set.IsWarmup)))
                .OrderByDescending(s => s.StartedAt).ThenByDescending(s => s.Id).Take(2)
            from set in db.Sets.Where(s => s.SessionExercise.SessionId == previous.Id && s.SessionExercise.ExerciseId == exercise.Id)
            orderby previous.StartedAt descending, previous.Id descending, set.CompletedAt, set.SetNumber, set.Id
            select new { ExerciseId = exercise.Id, SessionId = previous.Id, set.WeightKg, set.Reps, set.IsWarmup }
        ).ToListAsync(ct);

        return rows.GroupBy(r => r.ExerciseId).ToDictionary(g => g.Key, g => g.GroupBy(r => r.SessionId)
            .Select(session => (IReadOnlyList<LoggedSet>)session.Select(r => new LoggedSet(r.WeightKg, r.Reps, r.IsWarmup)).ToList())
            .ToList());
    }

    private async Task<TimeZoneInfo> ZoneAsync(CancellationToken ct) =>
        TimeZoneInfo.FindSystemTimeZoneById(
            await db.Profiles.Select(p => p.TimeZoneId).SingleOrDefaultAsync(ct) ?? UserProfile.DefaultTimeZoneId);

    private static DateOnly LocalDate(DateTimeOffset utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime);

    private static LoggedSet ToLogged(SetEntry s) => new(s.WeightKg, s.Reps, s.IsWarmup);

    private static decimal Round(decimal value) => Math.Round(value, 1);
}
