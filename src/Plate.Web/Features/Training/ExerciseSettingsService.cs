using Plate.Core.Domain;
using Plate.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Features.Training;

/// <summary>The signed-in user's notes and bar weights per exercise.</summary>
public class ExerciseSettingsService(AppDbContext db, ICurrentUser currentUser)
{
    public async Task<Dictionary<int, UserExerciseSettings>> ForAsync(IEnumerable<int> exerciseIds, CancellationToken ct)
    {
        var ids = exerciseIds.Distinct().ToList();
        return await db.ExerciseSettings.AsNoTracking().Where(x => ids.Contains(x.ExerciseId)).ToDictionaryAsync(x => x.ExerciseId, ct);
    }

    public Task<UserExerciseSettings?> GetAsync(int exerciseId, CancellationToken ct) =>
        db.ExerciseSettings.AsNoTracking().SingleOrDefaultAsync(x => x.ExerciseId == exerciseId, ct);

    /// <summary>Saves the note and bar weight; when both are empty the row is removed.</summary>
    public async Task SaveAsync(int exerciseId, string? note, decimal? barWeightKg, CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new InvalidOperationException("No signed-in user.");
        if (!await db.Exercises.AnyAsync(e => e.Id == exerciseId, ct))
        {
            throw new NotFoundException("Exercise not found.");
        }

        var trimmed = note?.Trim();
        if (trimmed?.Length > UserExerciseSettings.MaxNoteLength)
        {
            throw new ValidationException("Notes can be at most 200 characters.");
        }

        if (barWeightKg is < 0 or > 50)
        {
            throw new ValidationException("Bar weight must be between 0 and 50 kg.");
        }

        var row = await db.ExerciseSettings.SingleOrDefaultAsync(x => x.ExerciseId == exerciseId, ct);
        if (string.IsNullOrEmpty(trimmed) && barWeightKg is null)
        {
            if (row is not null)
            {
                db.ExerciseSettings.Remove(row);
            }
        }
        else
        {
            if (row is null)
            {
                row = new UserExerciseSettings { UserId = userId, ExerciseId = exerciseId };
                db.ExerciseSettings.Add(row);
            }

            row.Note = string.IsNullOrEmpty(trimmed) ? null : trimmed;
            row.BarWeightKg = barWeightKg;
        }

        await db.SaveChangesAsync(ct);
    }
}
