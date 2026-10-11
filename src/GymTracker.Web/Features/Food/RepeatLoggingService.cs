using GymTracker.Core.Nutrition;
using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Food;

/// <summary>
/// What this account did last time (SPEC 5.10): the amount per food, and which food it means by typed words (6.1).
/// Reads go through the owner query filters, so another account's entries and usual foods are never seen.
/// </summary>
public class RepeatLoggingService(AppDbContext db, ICurrentUser currentUser, TimeProvider time, ILogger<RepeatLoggingService> log)
{
    /// <summary>The last amount for each food that has one; foods without one are left out.</summary>
    public async Task<Dictionary<int, decimal>> LastAmountsAsync(IReadOnlyCollection<int> foodItemIds, CancellationToken ct)
    {
        if (foodItemIds.Count == 0)
        {
            return [];
        }

        var now = time.GetUtcNow();
        var since = now - LastAmount.Window;
        var ids = foodItemIds.Distinct().ToList();
        var rows = await db.FoodLog
            .Where(e => e.FoodItemId != null && ids.Contains(e.FoodItemId.Value) && e.CreatedAt >= since && e.Grams > 0)
            .Select(e => new { Id = e.FoodItemId!.Value, e.Grams, e.CreatedAt })
            .ToListAsync(ct);

        var result = new Dictionary<int, decimal>();
        foreach (var food in rows.GroupBy(r => r.Id))
        {
            if (LastAmount.Pick(food.Select(r => new AmountUse(r.Grams, r.CreatedAt)), now) is { } grams)
            {
                result[food.Key] = grams;
            }
        }

        return result;
    }
}
