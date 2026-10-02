using GymTracker.Core.Domain;
using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Training;

public class ExerciseService(AppDbContext db)
{
    /// <summary>The shared library plus the user's own exercises, grouped by primary muscle.</summary>
    public async Task<List<Exercise>> ListAsync(CancellationToken ct) =>
        (await db.Exercises.Where(e => !e.IsArchived).ToListAsync(ct))
            .OrderBy(e => e.PrimaryMuscle).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
