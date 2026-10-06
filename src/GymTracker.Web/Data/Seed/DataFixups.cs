using GymTracker.Core.Domain;
using GymTracker.Core.Text;
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

    /// <summary>
    /// Bulgarian names of USDA foods that were wrong in usda-foods.csv beyond a look-alike letter (a garbled word, a
    /// misspelling), by English name; the CSV is fixed too. Found on wizard after the Phase 11 deploy.
    /// </summary>
    private static readonly Dictionary<string, string> UsdaBgCorrections = new(StringComparer.Ordinal)
    {
        ["Pork, fresh, variety meats and by-products, leaf fat, raw"] = "Свинска вътрешна мас, сурова",
        ["Salad dressing, thousand island dressing, fat-free"] = "Дресинг Хиляда острова, обезмаслен",
        ["Salad dressing, thousand island dressing, reduced fat"] = "Дресинг Хиляда острова, с намалена масленост",
    };

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

        // Food names with a look-alike letter from the other alphabet inside a word ("чипc", "Мousse"), stored before
        // the seed normalised them. Postgres finds the candidates; MixedScript decides and fixes.
        var mixed = await db.FoodItems
            .FromSqlRaw("""
                SELECT * FROM "FoodItems"
                WHERE "Name" ~ '[А-Яа-я][A-Za-z]|[A-Za-z][А-Яа-я]' OR "NameBg" ~ '[А-Яа-я][A-Za-z]|[A-Za-z][А-Яа-я]'
                   OR "Brand" ~ '[А-Яа-я][A-Za-z]|[A-Za-z][А-Яа-я]'
                """)
            .IgnoreQueryFilters()
            .ToListAsync(ct);
        foreach (var food in mixed)
        {
            food.Name = MixedScript.Normalize(food.Name);
            food.NameBg = food.NameBg is null ? null : MixedScript.Normalize(food.NameBg);
            food.Brand = food.Brand is null ? null : MixedScript.Normalize(food.Brand);
        }

        await db.SaveChangesAsync(ct);

        foreach (var (name, bg) in UsdaBgCorrections)
        {
            await db.FoodItems.IgnoreQueryFilters()
                .Where(f => f.Source == FoodSource.Usda && f.Name == name && f.NameBg != bg)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.NameBg, bg), ct);
        }
    }
}
