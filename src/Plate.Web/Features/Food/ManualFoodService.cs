using Plate.Core.Domain;
using Plate.Core.Nutrition;
using Plate.Web.Data;

namespace Plate.Web.Features.Food;

/// <summary>
/// Saves a manual food and logs the portion eaten in one transaction (SPEC 7): when the entry is refused (too many
/// grams, a future day), no food is left behind, so a retry isn't told the barcode is already taken.
/// </summary>
public class ManualFoodService(AppDbContext db, FoodItemService foods, FoodLogService log, LabelScanService labels)
{
    public async Task<FoodItem> SaveAndLogAsync(DateOnly date, Meal meal, string? name, string? barcode, Per100g per100, decimal? servingGrams,
        decimal grams, bool share, int? labelScanId, CancellationToken ct, FoodUnit unit = FoodUnit.Grams)
    {
        if (FoodMath.ValidateGrams(grams) is { } error)
            throw new ValidationException(error, nameof(grams));
        if (servingGrams is { } serving && FoodMath.ValidateGrams(serving) is { } servingError)
            throw new ValidationException(servingError, nameof(servingGrams));

        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct) : null;
        var item = await foods.CreateManualAsync(name, barcode, per100, servingGrams, share, ct, unit);
        await labels.RecordSaveAsync(labelScanId, item, ct);
        await log.AddAsync([new NewEntry(date, meal, item.Name, grams, item.Per100(), item.Id, item.Barcode is null ? EntrySource.Manual : EntrySource.Barcode)], ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return item;
    }
}
