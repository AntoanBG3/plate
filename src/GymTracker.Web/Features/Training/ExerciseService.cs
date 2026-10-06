using GymTracker.Core.Domain;
using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Training;

public class ExerciseService(AppDbContext db, ICurrentUser currentUser)
{
    public const int MaxNameLength = 60;

    /// <summary>The shared library plus the user's own exercises, grouped by primary muscle.</summary>
    public async Task<List<Exercise>> ListAsync(CancellationToken ct) =>
        (await db.Exercises.Where(e => !e.IsArchived).ToListAsync(ct))
            .OrderBy(e => e.PrimaryMuscle).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>One exercise from the shared library or the user's own; another user's custom exercise is not found.</summary>
    public async Task<Exercise> GetAsync(int id, CancellationToken ct) =>
        await db.Exercises.SingleOrDefaultAsync(e => e.Id == id, ct) ?? throw new NotFoundException("Exercise not found.");

    /// <summary>The user's own exercises still in use, by name (Settings, custom exercises).</summary>
    public async Task<List<Exercise>> ListOwnAsync(CancellationToken ct) =>
        (await db.Exercises.Where(e => e.OwnerUserId == UserId && !e.IsArchived).ToListAsync(ct))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public async Task<Exercise> CreateAsync(string name, Muscle primary, Equipment equipment, IReadOnlyList<Muscle> secondary, CancellationToken ct)
    {
        var clean = await CheckNameAsync(name, null, ct);
        var exercise = new Exercise
        {
            Name = clean,
            PrimaryMuscle = primary,
            Equipment = equipment,
            SecondaryMuscles = secondary.Where(m => m != primary).Distinct().ToList(),
            OwnerUserId = UserId,
        };
        db.Exercises.Add(exercise);
        await db.SaveChangesAsync(ct);
        return exercise;
    }

    public async Task RenameAsync(int id, string name, CancellationToken ct)
    {
        var exercise = await OwnAsync(id, ct);
        exercise.Name = await CheckNameAsync(name, id, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Hides the exercise from the picker; past workouts and templates that use it keep it.</summary>
    public async Task ArchiveAsync(int id, CancellationToken ct)
    {
        var exercise = await OwnAsync(id, ct);
        exercise.IsArchived = true;
        await db.SaveChangesAsync(ct);
    }

    private string UserId => currentUser.UserId ?? throw new InvalidOperationException("No signed-in user.");

    /// <summary>The user's own exercise; the shared library and other users' exercises can't be changed.</summary>
    private async Task<Exercise> OwnAsync(int id, CancellationToken ct) =>
        await db.Exercises.SingleOrDefaultAsync(e => e.Id == id && e.OwnerUserId == UserId && !e.IsArchived, ct)
        ?? throw new NotFoundException("Exercise not found.");

    /// <summary>A trimmed name of 1 to 60 characters that no library or own exercise already has.</summary>
    private async Task<string> CheckNameAsync(string? name, int? exceptId, CancellationToken ct)
    {
        var clean = name?.Trim() ?? "";
        if (clean.Length is 0 or > MaxNameLength)
        {
            throw new ValidationException(new Core.Text.LocalText("A name is 1 to {0} characters.", MaxNameLength));
        }

        var lower = clean.ToLowerInvariant();
        var taken = await db.Exercises.AnyAsync(e => e.Id != exceptId && !e.IsArchived && e.Name.ToLower() == lower, ct);
        if (taken)
        {
            throw new ValidationException(new Core.Text.LocalText("There is already an exercise called {0}.", clean));
        }

        return clean;
    }
}
