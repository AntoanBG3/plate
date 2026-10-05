using GymTracker.Core.Domain;
using GymTracker.Core.Training;
using GymTracker.Web.Data;
using GymTracker.Web.Features.Profile;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GymTracker.Web.Features.Training;

public record LogSetInput(
    Guid ClientId,
    int SessionExerciseId,
    decimal WeightKg,
    int Reps,
    decimal? Rpe,
    bool IsWarmup,
    DateTimeOffset? CompletedAt);

public record SessionSummary(WorkoutSession Session, TimeSpan Duration, int WorkingSets);

public record HistoryItem(int Id, string Name, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, int WorkingSets);

public class SessionService(AppDbContext db, ICurrentUser currentUser, ProfileLookup profiles, TimeProvider time)
{
    public Task<WorkoutSession?> GetActiveAsync(CancellationToken ct) =>
        db.Sessions.Include(s => s.Template).SingleOrDefaultAsync(s => s.EndedAt == null, ct);

    public async Task<WorkoutSession> GetAsync(int id, CancellationToken ct) =>
        await db.Sessions
            .Include(s => s.Template)
            .Include(s => s.Exercises.OrderBy(e => e.SortOrder)).ThenInclude(e => e.Exercise)
            .Include(s => s.Exercises).ThenInclude(e => e.Sets.OrderBy(x => x.IsWarmup ? 0 : 1).ThenBy(x => x.SetNumber))
            .AsSplitQuery()
            .SingleOrDefaultAsync(s => s.Id == id, ct)
        ?? throw new NotFoundException();

    /// <summary>
    /// Starts a session from a template, or a freestyle one when templateId is null.
    /// If a session is already active it is returned instead, since only one may be active.
    /// </summary>
    public async Task<WorkoutSession> StartAsync(int? templateId, CancellationToken ct)
    {
        var active = await GetActiveAsync(ct);
        if (active is not null)
        {
            return active;
        }

        var userId = RequireUserId();
        var session = new WorkoutSession { UserId = userId, StartedAt = time.GetUtcNow() };

        if (templateId is { } id)
        {
            var template = await db.Templates
                .Include(t => t.Exercises).ThenInclude(e => e.Exercise)
                .SingleOrDefaultAsync(t => t.Id == id && !t.IsArchived, ct)
                ?? throw new NotFoundException("Template not found.");

            var defaultIncrement = await profiles.DefaultIncrementAsync(ct);
            session.TemplateId = template.Id;
            session.Exercises = template.Exercises
                .Where(e => !e.Exercise.IsArchived)
                .OrderBy(e => e.SortOrder)
                .Select((e, i) => new SessionExercise
                {
                    ExerciseId = e.ExerciseId,
                    SortOrder = i,
                    RepMin = e.RepMin,
                    RepMax = e.RepMax,
                    WorkingSets = e.WorkingSets,
                    WeightIncrementKg = e.WeightIncrementKg ?? defaultIncrement,
                    RestSeconds = e.RestSeconds,
                })
                .ToList();
        }

        db.Sessions.Add(session);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Another request started a session at the same moment; use that one.
            db.ChangeTracker.Clear();
            return await GetActiveAsync(ct) ?? throw new InvalidOperationException("Active session vanished.", ex);
        }

        return session;
    }

    /// <summary>
    /// Records a set. Idempotent on ClientId: a retry of the same set returns the stored set
    /// instead of creating a second one.
    /// </summary>
    public async Task<SetEntry> LogSetAsync(LogSetInput input, CancellationToken ct)
    {
        var existing = await FindByClientIdAsync(input.ClientId, ct);
        if (existing is not null)
        {
            return existing;
        }

        Validate(input.WeightKg, input.Reps, input.Rpe);

        var exercise = await db.SessionExercises.Include(e => e.Session).SingleOrDefaultAsync(e => e.Id == input.SessionExerciseId, ct)
            ?? throw new NotFoundException();

        // Numbered in the order sets are logged, separately for warmups and working sets.
        var lastNumber = await db.Sets
            .Where(s => s.SessionExerciseId == exercise.Id && s.IsWarmup == input.IsWarmup)
            .MaxAsync(s => (int?)s.SetNumber, ct) ?? 0;

        var set = new SetEntry
        {
            ClientId = input.ClientId,
            SessionExerciseId = exercise.Id,
            SetNumber = lastNumber + 1,
            WeightKg = input.WeightKg,
            Reps = input.Reps,
            Rpe = input.Rpe,
            IsWarmup = input.IsWarmup,
            CompletedAt = CompletedAt(input.CompletedAt, exercise.Session.StartedAt),
        };
        db.Sets.Add(set);

        // A set beyond the rows the server knows of (sent by an older page from the offline queue) grows the count,
        // so the row it was logged in stays on screen.
        var logged = await db.Sets.CountAsync(s => s.SessionExerciseId == exercise.Id && s.IsWarmup == input.IsWarmup, ct) + 1;
        if (input.IsWarmup)
        {
            exercise.WarmupSlots = Math.Max(exercise.WarmupSlots, logged);
        }
        else
        {
            exercise.WorkingSets = Math.Max(exercise.WorkingSets, logged);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent retry of the same set won the race.
            db.ChangeTracker.Clear();
            return await FindByClientIdAsync(input.ClientId, ct) ?? throw new NotFoundException();
        }

        return set;
    }

    public async Task<SetEntry> UpdateSetAsync(int setId, decimal weightKg, int reps, decimal? rpe, CancellationToken ct)
    {
        Validate(weightKg, reps, rpe);
        var set = await db.Sets.SingleOrDefaultAsync(s => s.Id == setId, ct) ?? throw new NotFoundException();
        set.WeightKg = weightKg;
        set.Reps = reps;
        set.Rpe = rpe;
        await db.SaveChangesAsync(ct);
        return set;
    }

    public async Task<SetEntry> DeleteSetAsync(int setId, CancellationToken ct)
    {
        var set = await db.Sets.SingleOrDefaultAsync(s => s.Id == setId, ct) ?? throw new NotFoundException();
        set.DeletedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return set;
    }

    public const int MaxWorkingSets = 20;
    public const int MaxWarmupSlots = 10;

    /// <summary>Adds an empty working-set or warm-up row to a session exercise. Stored, so a reload keeps it (SPEC 7).</summary>
    public async Task<SessionExercise> AddSlotAsync(int sessionExerciseId, bool warmup, CancellationToken ct)
    {
        var exercise = await GetExerciseAsync(sessionExerciseId, ct);
        var logged = exercise.Sets.Count(s => s.IsWarmup == warmup);
        if (warmup)
        {
            exercise.WarmupSlots = Math.Min(MaxWarmupSlots, Math.Max(exercise.WarmupSlots, logged) + 1);
        }
        else
        {
            exercise.WorkingSets = Math.Min(MaxWorkingSets, Math.Max(exercise.WorkingSets, logged) + 1);
        }

        await db.SaveChangesAsync(ct);
        return exercise;
    }

    /// <summary>
    /// Removes one empty row. False when every row of that kind holds a logged set, or when it would leave no working set.
    /// </summary>
    public async Task<bool> RemoveSlotAsync(int sessionExerciseId, bool warmup, CancellationToken ct)
    {
        var exercise = await GetExerciseAsync(sessionExerciseId, ct);
        var logged = exercise.Sets.Count(s => s.IsWarmup == warmup);
        if (warmup ? exercise.WarmupSlots <= logged : exercise.WorkingSets <= Math.Max(1, logged))
        {
            return false;
        }

        if (warmup)
        {
            exercise.WarmupSlots--;
        }
        else
        {
            exercise.WorkingSets--;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The weight of the last working set logged on this session exercise, or null.</summary>
    public Task<decimal?> LastWorkingWeightAsync(int sessionExerciseId, CancellationToken ct) =>
        db.Sets
            .Where(s => s.SessionExerciseId == sessionExerciseId && !s.IsWarmup)
            .OrderByDescending(s => s.SetNumber)
            .Select(s => (decimal?)s.WeightKg)
            .FirstOrDefaultAsync(ct);

    public async Task<SessionExercise> GetExerciseAsync(int sessionExerciseId, CancellationToken ct) =>
        await db.SessionExercises
            .Include(e => e.Exercise)
            .Include(e => e.Sets.OrderBy(x => x.IsWarmup ? 0 : 1).ThenBy(x => x.SetNumber))
            .SingleOrDefaultAsync(e => e.Id == sessionExerciseId, ct)
        ?? throw new NotFoundException();

    public async Task AddExerciseAsync(int sessionId, int exerciseId, CancellationToken ct)
    {
        var session = await db.Sessions.Include(s => s.Exercises).SingleOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new NotFoundException();
        var exercise = await db.Exercises.SingleOrDefaultAsync(e => e.Id == exerciseId && !e.IsArchived, ct)
            ?? throw new NotFoundException("Exercise not found.");

        session.Exercises.Add(new SessionExercise
        {
            ExerciseId = exercise.Id,
            SortOrder = session.Exercises.Count == 0 ? 0 : session.Exercises.Max(e => e.SortOrder) + 1,
            RepMin = 8,
            RepMax = 12,
            WorkingSets = 3,
            WeightIncrementKg = await profiles.DefaultIncrementAsync(ct),
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Replaces the exercise in a session slot, keeping its sets, rep range, increment and rest. Only before any set
    /// is logged on it, so history never mixes two exercises. A set deleted recently enough to be undone counts too,
    /// since undoing it after a swap would put it under the new exercise. The template is not changed.
    /// </summary>
    public async Task<int> SwapExerciseAsync(int sessionExerciseId, int exerciseId, CancellationToken ct)
    {
        var item = await db.SessionExercises.SingleOrDefaultAsync(e => e.Id == sessionExerciseId, ct)
            ?? throw new NotFoundException();
        var undoableSince = time.GetUtcNow() - GymTracker.Web.Features.Undo.UndoService.Window;
        var hasSets = await db.Sets.IgnoreQueryFilters([AppDbContext.SoftDeleteFilter])
            .AnyAsync(x => x.SessionExerciseId == item.Id && (x.DeletedAt == null || x.DeletedAt > undoableSince), ct);
        if (hasSets)
        {
            throw new ValidationException("Only an exercise with no sets logged can be swapped.");
        }

        var exercise = await db.Exercises.SingleOrDefaultAsync(e => e.Id == exerciseId && !e.IsArchived, ct)
            ?? throw new NotFoundException("Exercise not found.");
        item.ExerciseId = exercise.Id;
        await db.SaveChangesAsync(ct);
        return item.SessionId;
    }

    public async Task<int> MoveExerciseAsync(int sessionExerciseId, int direction, CancellationToken ct)
    {
        var item = await db.SessionExercises.SingleOrDefaultAsync(e => e.Id == sessionExerciseId, ct) ?? throw new NotFoundException();
        var siblings = await db.SessionExercises.Where(e => e.SessionId == item.SessionId).OrderBy(e => e.SortOrder).ThenBy(e => e.Id).ToListAsync(ct);
        if (TemplateService.Reorder(siblings, e => e.Id == sessionExerciseId, direction, (e, i) => e.SortOrder = i))
        {
            await db.SaveChangesAsync(ct);
        }

        return item.SessionId;
    }

    /// <summary>Removes an exercise from the session and hides its sets; undo can restore both.</summary>
    public async Task<int> RemoveExerciseAsync(int sessionExerciseId, CancellationToken ct)
    {
        var item = await db.SessionExercises.SingleOrDefaultAsync(e => e.Id == sessionExerciseId, ct) ?? throw new NotFoundException();
        item.DeletedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return item.SessionId;
    }

    public async Task SetNotesAsync(int sessionId, string? notes, CancellationToken ct)
    {
        var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId, ct) ?? throw new NotFoundException();
        var trimmed = notes?.Trim();
        if (trimmed?.Length > 2000)
        {
            throw new ValidationException("Notes can be at most 2000 characters.");
        }

        session.Notes = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        await db.SaveChangesAsync(ct);
    }

    public async Task<SessionSummary> FinishAsync(int sessionId, CancellationToken ct)
    {
        var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId, ct) ?? throw new NotFoundException();
        session.EndedAt ??= time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return await SummaryAsync(sessionId, ct);
    }

    public async Task<SessionSummary> SummaryAsync(int sessionId, CancellationToken ct)
    {
        var session = await GetAsync(sessionId, ct);
        var end = session.EndedAt ?? time.GetUtcNow();
        var workingSets = session.Exercises.Sum(e => e.Sets.Count(s => !s.IsWarmup));
        return new SessionSummary(session, end - session.StartedAt, workingSets);
    }

    /// <summary>Permanently deletes a session and everything logged in it.</summary>
    public async Task DeleteAsync(int sessionId, CancellationToken ct)
    {
        var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId, ct) ?? throw new NotFoundException();
        db.Sessions.Remove(session);
        await db.SaveChangesAsync(ct);
    }

    public Task<List<HistoryItem>> HistoryAsync(int take, CancellationToken ct) =>
        db.Sessions
            .OrderByDescending(s => s.StartedAt)
            .Take(take)
            .Select(s => new HistoryItem(
                s.Id,
                s.Template != null ? s.Template.Name : "Freestyle",
                s.StartedAt,
                s.EndedAt,
                s.Exercises.SelectMany(e => e.Sets).Count(x => !x.IsWarmup)))
            .ToListAsync(ct);

    /// <summary>Sessions finished since <paramref name="since"/>, newest first (Today's "Done today").</summary>
    public Task<List<HistoryItem>> FinishedSinceAsync(DateTimeOffset since, CancellationToken ct) =>
        db.Sessions
            .Where(s => s.EndedAt != null && s.EndedAt >= since)
            .OrderByDescending(s => s.EndedAt)
            .Select(s => new HistoryItem(
                s.Id,
                s.Template != null ? s.Template.Name : "Freestyle",
                s.StartedAt,
                s.EndedAt,
                s.Exercises.SelectMany(e => e.Sets).Count(x => !x.IsWarmup)))
            .ToListAsync(ct);

    public Task<int?> LastCompletedTemplateIdAsync(CancellationToken ct) =>
        db.Sessions
            .Where(s => s.EndedAt != null && s.TemplateId != null)
            .OrderByDescending(s => s.StartedAt)
            .Select(s => s.TemplateId)
            .FirstOrDefaultAsync(ct);

    private async Task<SetEntry?> FindByClientIdAsync(Guid clientId, CancellationToken ct)
    {
        // Look past the query filter so another user's ClientId is recognised, then refuse it explicitly.
        var set = await db.Sets.IgnoreQueryFilters()
            .Include(s => s.SessionExercise).ThenInclude(e => e.Session)
            .SingleOrDefaultAsync(s => s.ClientId == clientId, ct);

        if (set is null)
        {
            return null;
        }

        if (set.SessionExercise.Session.UserId != RequireUserId())
        {
            throw new NotFoundException();
        }

        return set;
    }

    /// <summary>
    /// The time the phone says the set was done, so a set sent late from the offline queue keeps its real time.
    /// A clock that is clearly wrong (before the session or in the future) falls back to now.
    /// </summary>
    private DateTimeOffset CompletedAt(DateTimeOffset? client, DateTimeOffset sessionStart)
    {
        var now = time.GetUtcNow();
        if (client is { } t && t >= sessionStart.AddMinutes(-1) && t <= now.AddMinutes(5))
        {
            return t.ToUniversalTime();
        }

        return now;
    }

    private static void Validate(decimal weightKg, int reps, decimal? rpe)
    {
        if (SetRules.Validate(weightKg, reps, rpe) is { } error)
        {
            throw new ValidationException(error);
        }
    }

    private string RequireUserId() =>
        currentUser.UserId ?? throw new InvalidOperationException("No signed-in user.");

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
