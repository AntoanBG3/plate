using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Undo;

public enum UndoKind
{
    Entry,
    Set,
    SessionExercise,
    TemplateExercise,
}

/// <summary>Brings back a soft-deleted row of the current user. The owner filter stays on, so other users' rows are not found.</summary>
public class UndoService(AppDbContext db, TimeProvider time)
{
    /// <summary>How long after a delete undo is still accepted. The toast shows for a few seconds; this allows for slow taps and retries.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private static readonly string[] SoftDelete = [AppDbContext.SoftDeleteFilter];

    public async Task RestoreAsync(UndoKind kind, int id, CancellationToken ct)
    {
        switch (kind)
        {
            case UndoKind.Entry:
                var entry = await db.FoodLog.IgnoreQueryFilters(SoftDelete).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
                if (Restorable(entry.DeletedAt))
                {
                    entry.DeletedAt = null;
                }

                break;

            case UndoKind.Set:
                var set = await db.Sets.IgnoreQueryFilters(SoftDelete).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
                if (Restorable(set.DeletedAt))
                {
                    set.DeletedAt = null;

                    // Another set may have taken its number meanwhile; number the live ones 1..n again.
                    var siblings = await db.Sets.Where(x => x.SessionExerciseId == set.SessionExerciseId && x.IsWarmup == set.IsWarmup && x.Id != set.Id).ToListAsync(ct);
                    var ordered = siblings.Append(set).OrderBy(x => x.SetNumber).ThenBy(x => x.CompletedAt).ThenBy(x => x.Id).ToList();
                    for (var i = 0; i < ordered.Count; i++)
                    {
                        ordered[i].SetNumber = i + 1;
                    }
                }

                break;

            case UndoKind.SessionExercise:
                var item = await db.SessionExercises.IgnoreQueryFilters(SoftDelete).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
                if (Restorable(item.DeletedAt))
                {
                    item.DeletedAt = null;
                    var others = await db.SessionExercises.Where(x => x.SessionId == item.SessionId && x.Id != item.Id).ToListAsync(ct);
                    Renumber(others.Append(item), x => x.SortOrder, x => x.Id, (x, i) => x.SortOrder = i);
                }

                break;

            case UndoKind.TemplateExercise:
                var te = await db.TemplateExercises.IgnoreQueryFilters(SoftDelete).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
                if (Restorable(te.DeletedAt))
                {
                    te.DeletedAt = null;
                    var others = await db.TemplateExercises.Where(x => x.TemplateId == te.TemplateId && x.Id != te.Id).ToListAsync(ct);
                    Renumber(others.Append(te), x => x.SortOrder, x => x.Id, (x, i) => x.SortOrder = i);
                }

                break;

            default:
                throw new NotFoundException();
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>True when the row is deleted and still inside the window; false when it's already live (a repeated undo).</summary>
    private bool Restorable(DateTimeOffset? deletedAt)
    {
        if (deletedAt is null)
        {
            return false;
        }

        if (time.GetUtcNow() - deletedAt.Value > Window)
        {
            throw new ValidationException("It's too late to undo that.");
        }

        return true;
    }

    private static void Renumber<T>(IEnumerable<T> items, Func<T, int> order, Func<T, int> id, Action<T, int> set)
    {
        var ordered = items.OrderBy(order).ThenBy(id).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            set(ordered[i], i);
        }
    }
}
