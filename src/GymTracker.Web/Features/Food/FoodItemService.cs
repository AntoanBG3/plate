using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Food;

/// <summary>A food from a name search, with how closely it matched (0 to 1).</summary>
public record FoodMatch(int Id, string Name, string? NameBg, string? Brand, decimal KcalPer100g, decimal ProteinPer100g, decimal CarbsPer100g, decimal FatPer100g, decimal? FiberPer100g, decimal? SaturatedFatPer100g, decimal? DefaultServingGrams, bool Own, double Score)
{
    public Per100g Per100 => new(KcalPer100g, ProteinPer100g, CarbsPer100g, FatPer100g, FiberPer100g, SaturatedFatPer100g);

    /// <summary>The Bulgarian name for a Bulgarian interface when there is one.</summary>
    public string DisplayName => Localization.Localizer.IsBulgarian && !string.IsNullOrEmpty(NameBg) ? NameBg : Name;
}

public record BarcodeResult(LookupStatus Status, FoodItem? Item);

public class FoodItemService(AppDbContext db, ICurrentUser currentUser, IFoodDatabase foodDatabase)
{
    /// <summary>
    /// The user's own foods first, then shared ones (USDA, Open Food Facts cache, manual barcode items),
    /// ranked by trigram word similarity of the English or Bulgarian name.
    /// </summary>
    public async Task<List<FoodMatch>> SearchAsync(string query, int take, double minScore, CancellationToken ct)
    {
        var q = query.Trim();
        if (q.Length < 2)
        {
            return [];
        }

        // Raw SQL bypasses the global query filter, so ownership is checked explicitly here.
        var userId = currentUser.UserId ?? "";
        return await db.Database.SqlQuery<FoodMatch>($"""
            SELECT f."Id", f."Name", f."NameBg", f."Brand", f."KcalPer100g", f."ProteinPer100g", f."CarbsPer100g", f."FatPer100g",
                   f."FiberPer100g", f."SaturatedFatPer100g", f."DefaultServingGrams", f."OwnerUserId" IS NOT NULL AS "Own",
                   GREATEST(word_similarity({q}, f."Name"), word_similarity({q}, COALESCE(f."NameBg", '')))::float8 AS "Score"
            FROM "FoodItems" f
            WHERE (f."OwnerUserId" IS NULL OR f."OwnerUserId" = {userId})
              AND GREATEST(word_similarity({q}, f."Name"), word_similarity({q}, COALESCE(f."NameBg", ''))) >= {minScore}
            ORDER BY "Own" DESC, "Score" DESC, length(f."Name")
            LIMIT {take}
            """).ToListAsync(ct);
    }

    /// <summary>The food's name as shown (Bulgarian when the reader uses it), or null when it isn't visible to this user.</summary>
    public async Task<string?> DisplayNameOrNullAsync(int id, CancellationToken ct) =>
        (await db.FoodItems.SingleOrDefaultAsync(f => f.Id == id, ct)) is { } item ? Localization.FoodNames.DisplayName(item) : null;

    public async Task<FoodItem> GetAsync(int id, CancellationToken ct) =>
        await db.FoodItems.SingleOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException("Food not found.");

    /// <summary>The cached food for a barcode, else an Open Food Facts lookup that is cached as a shared food.</summary>
    public async Task<BarcodeResult> LookupBarcodeAsync(string barcode, CancellationToken ct)
    {
        if (!Barcodes.IsValid(barcode))
        {
            throw new ValidationException("That isn't a valid EAN or UPC barcode.");
        }

        var cached = await db.FoodItems.SingleOrDefaultAsync(f => f.Barcode == barcode, ct);
        if (cached is not null)
        {
            return new BarcodeResult(LookupStatus.Found, cached);
        }

        var lookup = await foodDatabase.GetByBarcodeAsync(barcode, ct);
        if (lookup.Product is not { } p)
        {
            return new BarcodeResult(lookup.Status, null);
        }

        return new BarcodeResult(LookupStatus.Found, await CacheAsync(p, ct));
    }

    public Task<ProductSearch> SearchOpenFoodFactsAsync(string query, CancellationToken ct) =>
        query.Trim().Length < 2 ? Task.FromResult(new ProductSearch(LookupStatus.NotFound, [])) : foodDatabase.SearchAsync(query.Trim(), ct);

    /// <summary>Stores an Open Food Facts product as a shared food so repeat scans need no API call.</summary>
    public async Task<FoodItem> CacheAsync(ProductInfo p, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(p.Barcode) && await db.FoodItems.IgnoreQueryFilters().SingleOrDefaultAsync(f => f.Barcode == p.Barcode, ct) is { } existing)
        {
            return existing;
        }

        var item = new FoodItem
        {
            Name = Clip(p.Name),
            Brand = p.Brand is null ? null : Clip(p.Brand),
            Barcode = Barcodes.IsValid(p.Barcode) ? p.Barcode : null,
            KcalPer100g = p.Per100.Kcal,
            ProteinPer100g = p.Per100.Protein,
            CarbsPer100g = p.Per100.Carbs,
            FatPer100g = p.Per100.Fat,
            FiberPer100g = p.Per100.Fiber,
            SaturatedFatPer100g = p.Per100.SaturatedFat,
            DefaultServingGrams = p.ServingGrams,
            Source = FoodSource.OpenFoodFacts,
        };
        db.FoodItems.Add(item);
        await db.SaveChangesAsync(ct);
        return item;
    }

    /// <summary>
    /// A food typed in by hand. With a barcode it is shared, so the other user can scan the same product;
    /// without one it belongs to this user only.
    /// </summary>
    public async Task<FoodItem> CreateManualAsync(string name, string? barcode, Per100g per100, decimal? servingGrams, CancellationToken ct)
    {
        name = name?.Trim() ?? "";
        if (name.Length is 0 or > 200)
        {
            throw new ValidationException("Name must be 1 to 200 characters.");
        }

        if (FoodMath.Validate(servingGrams ?? 100, per100) is { } error)
        {
            throw new ValidationException(error);
        }

        barcode = string.IsNullOrWhiteSpace(barcode) ? null : barcode.Trim();
        if (barcode is not null && !Barcodes.IsValid(barcode))
        {
            throw new ValidationException("That isn't a valid EAN or UPC barcode.");
        }

        if (barcode is not null && await db.FoodItems.IgnoreQueryFilters().AnyAsync(f => f.Barcode == barcode, ct))
        {
            throw new ValidationException("A food with that barcode already exists.");
        }

        var item = new FoodItem
        {
            OwnerUserId = barcode is null ? currentUser.UserId ?? throw new InvalidOperationException("No signed-in user.") : null,
            Name = name,
            Barcode = barcode,
            KcalPer100g = per100.Kcal,
            ProteinPer100g = per100.Protein,
            CarbsPer100g = per100.Carbs,
            FatPer100g = per100.Fat,
            FiberPer100g = per100.Fiber,
            SaturatedFatPer100g = per100.SaturatedFat,
            DefaultServingGrams = servingGrams,
            Source = FoodSource.Manual,
        };
        db.FoodItems.Add(item);
        await db.SaveChangesAsync(ct);
        return item;
    }

    private static string Clip(string s) => s.Length > 200 ? s[..200] : s;
}
