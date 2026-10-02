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
        db.Sets.Remove(set);
        await db.SaveChangesAsync(ct);
        return set;
    }

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

    /// <summary>Removes an exercise and its logged sets from the session.</summary>
    public async Task<int> RemoveExerciseAsync(int sessionExerciseId, CancellationToken ct)
    {
        var item = await db.SessionExercises.SingleOrDefaultAsync(e => e.Id == sessionExerciseId, ct) ?? throw new NotFoundException();
        db.SessionExercises.Remove(item);
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
