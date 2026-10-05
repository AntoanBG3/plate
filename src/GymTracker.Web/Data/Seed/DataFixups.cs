using GymTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Data.Seed;

/// <summary>
/// One-off corrections to stored data, run at startup after the seeds. Each is idempotent: run again, it finds nothing
/// to change.
/// </summary>
public static class DataFixups
{
    /// <summary>The default names quick entries used to be saved with, in English and Bulgarian.</summary>
    private static readonly string[] OldQuickNames = ["Quick add", "Бързо добавяне"];

    public static async Task RunAsync(AppDbContext db, CancellationToken ct)
    {
        // Quick entries left unnamed were stored as "Quick add" in the language of the moment, so the other
        // language showed it untranslated; they are now stored empty and named when shown (Phase 11).
        await db.FoodLog.IgnoreQueryFilters()
            .Where(e => e.Source == EntrySource.Quick && OldQuickNames.Contains(e.Description))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Description, ""), ct);
        await db.SavedMealItems.IgnoreQueryFilters()
            .Where(i => i.Grams == 0 && i.FoodItemId == null && OldQuickNames.Contains(i.Description))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Description, ""), ct);
    }
}
