using GymTracker.Core.Domain;
using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Training;

public record TemplateExerciseInput(int WorkingSets, int RepMin, int RepMax, decimal? WeightIncrementKg, int RestSeconds);

public class TemplateService(AppDbContext db, ICurrentUser currentUser)
{
    public Task<List<WorkoutTemplate>> ListAsync(CancellationToken ct) =>
        db.Templates
            .Where(t => !t.IsArchived)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Id)
            .Include(t => t.Exercises.OrderBy(e => e.SortOrder)).ThenInclude(e => e.Exercise)
            .AsSplitQuery()
            .ToListAsync(ct);

    public async Task<WorkoutTemplate> GetAsync(int id, CancellationToken ct) =>
        await db.Templates
            .Include(t => t.Exercises.OrderBy(e => e.SortOrder)).ThenInclude(e => e.Exercise)
            .SingleOrDefaultAsync(t => t.Id == id && !t.IsArchived, ct)
        ?? throw new NotFoundException();

    public async Task<WorkoutTemplate> CreateAsync(string name, CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new InvalidOperationException("No signed-in user.");
        var maxOrder = await db.Templates.MaxAsync(t => (int?)t.SortOrder, ct) ?? -1;
        var template = new WorkoutTemplate { UserId = userId, Name = CleanName(name), SortOrder = maxOrder + 1 };
        db.Templates.Add(template);
        await db.SaveChangesAsync(ct);
        return template;
    }

    public async Task RenameAsync(int id, string name, CancellationToken ct)
    {
        var template = await FindAsync(id, ct);
        template.Name = CleanName(name);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Archived templates disappear from lists but past sessions keep their link.</summary>
    public async Task ArchiveAsync(int id, CancellationToken ct)
    {
        var template = await FindAsync(id, ct);
        template.IsArchived = true;
        await db.SaveChangesAsync(ct);
    }

    public async Task MoveAsync(int id, int direction, CancellationToken ct)
    {
        var templates = await db.Templates.Where(t => !t.IsArchived).OrderBy(t => t.SortOrder).ThenBy(t => t.Id).ToListAsync(ct);
        if (Reorder(templates, t => t.Id == id, direction, (t, i) => t.SortOrder = i))
        {
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task AddExerciseAsync(int templateId, int exerciseId, CancellationToken ct)
    {
        var template = await GetAsync(templateId, ct);
        var exercise = await db.Exercises.SingleOrDefaultAsync(e => e.Id == exerciseId && !e.IsArchived, ct)
            ?? throw new NotFoundException("Exercise not found.");

        template.Exercises.Add(new TemplateExercise
        {
            ExerciseId = exercise.Id,
            SortOrder = template.Exercises.Count == 0 ? 0 : template.Exercises.Max(e => e.SortOrder) + 1,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateExerciseAsync(int templateExerciseId, TemplateExerciseInput input, CancellationToken ct)
    {
        Validate(input);
        var item = await FindExerciseAsync(templateExerciseId, ct);
        item.WorkingSets = input.WorkingSets;
        item.RepMin = input.RepMin;
        item.RepMax = input.RepMax;
        item.WeightIncrementKg = input.WeightIncrementKg;
        item.RestSeconds = input.RestSeconds;
        await db.SaveChangesAsync(ct);
    }

    public async Task MoveExerciseAsync(int templateExerciseId, int direction, CancellationToken ct)
    {
        var item = await FindExerciseAsync(templateExerciseId, ct);
        var siblings = await db.TemplateExercises.Where(e => e.TemplateId == item.TemplateId).OrderBy(e => e.SortOrder).ThenBy(e => e.Id).ToListAsync(ct);
        if (Reorder(siblings, e => e.Id == templateExerciseId, direction, (e, i) => e.SortOrder = i))
        {
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task RemoveExerciseAsync(int templateExerciseId, CancellationToken ct)
    {
        var item = await FindExerciseAsync(templateExerciseId, ct);
        db.TemplateExercises.Remove(item);
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> TemplateIdOfExerciseAsync(int templateExerciseId, CancellationToken ct) =>
        (await FindExerciseAsync(templateExerciseId, ct)).TemplateId;

    private async Task<WorkoutTemplate> FindAsync(int id, CancellationToken ct) =>
        await db.Templates.SingleOrDefaultAsync(t => t.Id == id && !t.IsArchived, ct) ?? throw new NotFoundException();

    private async Task<TemplateExercise> FindExerciseAsync(int id, CancellationToken ct) =>
        await db.TemplateExercises.SingleOrDefaultAsync(e => e.Id == id, ct) ?? throw new NotFoundException();

    private static string CleanName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > 100)
        {
            throw new ValidationException("Name must be 1 to 100 characters.");
        }

        return trimmed;
    }

    private static void Validate(TemplateExerciseInput input)
    {
        if (input.WorkingSets is < 1 or > 20)
        {
            throw new ValidationException("Working sets must be between 1 and 20.");
        }

        if (input.RepMin < 1 || input.RepMax > 100 || input.RepMin > input.RepMax)
        {
            throw new ValidationException("Rep range must be between 1 and 100, with the minimum not above the maximum.");
        }

        if (input.WeightIncrementKg is { } inc && (inc <= 0 || inc > 50))
        {
            throw new ValidationException("Increment must be above 0 and at most 50 kg.");
        }

        if (input.RestSeconds is < 0 or > 1800)
        {
            throw new ValidationException("Rest must be between 0 and 1800 seconds.");
        }
    }

    /// <summary>Swaps the matching item with its neighbour and renumbers. Returns false if it can't move.</summary>
    internal static bool Reorder<T>(List<T> ordered, Func<T, bool> match, int direction, Action<T, int> setOrder)
    {
        var index = ordered.FindIndex(x => match(x));
        var target = index + Math.Sign(direction);
        if (index < 0 || direction == 0 || target < 0 || target >= ordered.Count)
        {
            return false;
        }

        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        for (var i = 0; i < ordered.Count; i++)
        {
            setOrder(ordered[i], i);
        }

        return true;
    }
}
