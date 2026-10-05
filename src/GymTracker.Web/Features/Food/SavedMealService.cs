using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Core.Text;
using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Food;

public class SavedMealService(AppDbContext db, ICurrentUser currentUser, TimeProvider time)
{
    public Task<List<SavedMeal>> ListAsync(CancellationToken ct) =>
        db.SavedMeals.AsNoTracking().Include(m => m.Items.OrderBy(i => i.SortOrder)).OrderBy(m => m.Name).ToListAsync(ct);

    public async Task<SavedMeal> GetAsync(int id, CancellationToken ct) =>
        await db.SavedMeals.Include(m => m.Items.OrderBy(i => i.SortOrder)).SingleOrDefaultAsync(m => m.Id == id, ct)
        ?? throw new NotFoundException();

    /// <summary>Copies one meal of one day into a new saved meal.</summary>
    public async Task<SavedMeal> SaveFromMealAsync(DateOnly date, Meal meal, string name, CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new InvalidOperationException("No signed-in user.");
        var clean = CleanName(name);
        var entries = await db.FoodLog.Where(e => e.Date == date && e.Meal == meal).OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync(ct);
        if (entries.Count == 0)
        {
            throw new ValidationException("This meal has nothing logged yet.");
        }

        var saved = new SavedMeal
        {
            UserId = userId,
            Name = clean,
            CreatedAt = time.GetUtcNow(),
            Items = entries.Select((e, i) => new SavedMealItem
            {
                SortOrder = i,
                Description = e.Description,
                Grams = e.Grams,
                Kcal = e.Kcal,
                ProteinG = e.ProteinG,
                CarbsG = e.CarbsG,
                FatG = e.FatG,
                FiberG = e.FiberG,
                SaturatedFatG = e.SaturatedFatG,
                FoodItemId = e.FoodItemId,
                Source = e.Source,
            }).ToList(),
        };
        db.SavedMeals.Add(saved);
        await db.SaveChangesAsync(ct);
        return saved;
    }

    /// <summary>
    /// Logs every item into a meal, each at its grams from <paramref name="grams"/> (same order as the items; null keeps
    /// the saved amount). Quick items keep their numbers. A food link the user can no longer see is dropped.
    /// Returns how many entries were added.
    /// </summary>
    public async Task<int> LogAsync(int id, DateOnly date, Meal meal, IReadOnlyList<decimal?> grams, CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new InvalidOperationException("No signed-in user.");
        var saved = await GetAsync(id, ct);
        var foodIds = saved.Items.Where(i => i.FoodItemId != null).Select(i => i.FoodItemId!.Value).ToList();
        var visible = (await db.FoodItems.Where(f => foodIds.Contains(f.Id)).Select(f => f.Id).ToListAsync(ct)).ToHashSet();
        var now = time.GetUtcNow();

        for (var i = 0; i < saved.Items.Count; i++)
        {
            var item = saved.Items[i];
            var quick = item.Grams <= 0;
            var to = quick ? 0 : grams.ElementAtOrDefault(i) ?? item.Grams;
            if (!quick && (to <= 0 || to > FoodMath.MaxGrams))
            {
                throw new ValidationException(new LocalText("{0}: {1}", item.Description, FoodMath.Validate(to, new Per100g(0, 0, 0, 0))!));
            }

            var totals = FoodMath.Scale(new Totals(item.Kcal, item.ProteinG, item.CarbsG, item.FatG, item.FiberG, item.SaturatedFatG), item.Grams, to);
            db.FoodLog.Add(new FoodLogEntry
            {
                UserId = userId,
                Date = date,
                Meal = meal,
                Description = item.Description,
                Grams = Math.Round(to, 1),
                Kcal = totals.Kcal,
                ProteinG = totals.ProteinG,
                CarbsG = totals.CarbsG,
                FatG = totals.FatG,
                FiberG = totals.FiberG,
                SaturatedFatG = totals.SaturatedFatG,
                FoodItemId = item.FoodItemId is { } f && visible.Contains(f) ? f : null,
                Source = quick ? EntrySource.Quick : EntrySource.Recent,
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
        return saved.Items.Count;
    }

    public async Task RenameAsync(int id, string name, CancellationToken ct)
    {
        var saved = await GetAsync(id, ct);
        saved.Name = CleanName(name);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        var saved = await GetAsync(id, ct);
        db.SavedMeals.Remove(saved);
        await db.SaveChangesAsync(ct);
    }

    private static string CleanName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > SavedMeal.MaxNameLength)
        {
            throw new ValidationException("Name must be 1 to 60 characters.");
        }

        return trimmed;
    }
}
