using GymTracker.Core.Domain;
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

    /// <summary>
    /// The food this account means by each item's words (SPEC 6.1 step 1), by item index: its <c>NameOriginal</c> key, else
    /// its <c>NameEn</c> key. A row whose food the account can no longer see, or whose raw or cooked state contradicts the
    /// item's, is skipped.
    /// </summary>
    public async Task<Dictionary<int, FoodMatch>> UsualMatchesAsync(IReadOnlyList<ParsedFoodItem> items, CancellationToken ct)
    {
        var keys = items.Select(i => new[] { UsualFoods.Key(i.NameOriginal), UsualFoods.Key(i.NameEn) }).ToList();
        var all = keys.SelectMany(k => k).OfType<string>().Distinct().ToList();
        if (all.Count == 0)
        {
            return [];
        }

        var byPhrase = await db.UsualFoods.Where(u => all.Contains(u.Phrase)).ToDictionaryAsync(u => u.Phrase, u => u.FoodItemId, ct);
        var ids = byPhrase.Values.Distinct().ToList();
        var foods = await db.FoodItems.Where(f => ids.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct); // own or shared only

        var result = new Dictionary<int, FoodMatch>();
        for (var n = 0; n < items.Count; n++)
        {
            foreach (var key in keys[n])
            {
                if (key is not null && byPhrase.TryGetValue(key, out var id) && foods.TryGetValue(id, out var food) && !Contradicts(items[n].State, food))
                {
                    result[n] = Match(food);
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Remembers which food the account saved for its words (SPEC 6.1, usual foods): the latest save wins. Recording
    /// never gets in the way of logging food, so a failure is logged and dropped.
    /// </summary>
    public async Task RememberAsync(IReadOnlyList<(string Typed, int FoodItemId)> saved, CancellationToken ct)
    {
        var keyed = saved
            .Select(s => (Key: UsualFoods.Key(s.Typed), s.FoodItemId))
            .Where(s => s.Key is not null)
            .GroupBy(s => s.Key!)
            .Select(g => (Key: g.Key, g.Last().FoodItemId))
            .ToList();
        if (keyed.Count == 0)
        {
            return;
        }

        try
        {
            var ids = keyed.Select(k => k.FoodItemId).Distinct().ToList();
            var visible = (await db.FoodItems.Where(f => ids.Contains(f.Id)).Select(f => f.Id).ToListAsync(ct)).ToHashSet();
            var phrases = keyed.Select(k => k.Key).ToList();
            var existing = await db.UsualFoods.Where(u => phrases.Contains(u.Phrase)).ToDictionaryAsync(u => u.Phrase, ct);
            var now = time.GetUtcNow();
            foreach (var (key, foodItemId) in keyed.Where(k => visible.Contains(k.FoodItemId)))
            {
                if (existing.TryGetValue(key, out var row))
                {
                    row.FoodItemId = foodItemId;
                    row.LastUsedAt = now;
                }
                else
                {
                    db.UsualFoods.Add(new UsualFood { UserId = currentUser.UserId!, Phrase = key, FoodItemId = foodItemId, LastUsedAt = now });
                }
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Two confirms at once can both insert the same words, or the database can fail; the food is already logged,
            // so this only loses the memory. Detaching keeps a later save in the same request from retrying the rows.
            log.LogWarning(ex, "Could not remember usual foods.");
            foreach (var entry in db.ChangeTracker.Entries<UsualFood>().ToList())
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// The item says raw and the food's name says cooked, or the other way round: "пилешко филе на скара" is not the raw
    /// fillet confirmed for "пилешко филе". A state that isn't stated on either side never blocks.
    /// </summary>
    private static bool Contradicts(FoodState item, FoodItem food) =>
        (item, FoodStates.FromName(food.Name, food.NameBg)) is (FoodState.Raw, FoodState.Cooked) or (FoodState.Cooked, FoodState.Raw);

    private FoodMatch Match(FoodItem f) =>
        new(f.Id, f.Name, f.NameBg, f.Brand, f.KcalPer100g, f.ProteinPer100g, f.CarbsPer100g, f.FatPer100g, f.FiberPer100g, f.SaturatedFatPer100g,
            f.DefaultServingGrams, Own: f.OwnerUserId is not null, ByOther: FoodItemService.AddedByAnother(f, currentUser.UserId), Score: 1);
}
