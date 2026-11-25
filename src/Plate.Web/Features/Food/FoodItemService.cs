using System.Globalization;
using Plate.Core.Domain;
using Plate.Core.Nutrition;
using Plate.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Features.Food;

/// <summary>A food from a name search, with how closely it matched (0 to 1).</summary>
/// <param name="ByOther">A shared manual food another account added (SPEC 7); says nothing about who.</param>
public record FoodMatch(int Id, string Name, string? NameBg, string? Brand, decimal KcalPer100g, decimal ProteinPer100g, decimal CarbsPer100g, decimal FatPer100g, decimal? FiberPer100g, decimal? SaturatedFatPer100g, decimal? DefaultServingGrams, bool Own, bool ByOther, double Score,
    decimal? PieceGrams = null, decimal? PackGrams = null, bool ValuesEstimated = false, bool PerMillilitre = false)
{
    public Per100g Per100 => new(KcalPer100g, ProteinPer100g, CarbsPer100g, FatPer100g, FiberPer100g, SaturatedFatPer100g);

    /// <summary>The food's unit; the search SQL reads it as <see cref="PerMillilitre"/>, since the column is text.</summary>
    public FoodUnit Unit => PerMillilitre ? FoodUnit.Millilitres : FoodUnit.Grams;

    public UnitWeights Weights => new(PieceGrams, PackGrams, DefaultServingGrams);

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
    public async Task<List<FoodMatch>> SearchAsync(string query, int take, double minScore, CancellationToken ct, bool includeBrand = false)
    {
        var q = query.Trim();
        if (q.Length < 2)
        {
            return [];
        }

        // Raw SQL bypasses the global query filter, so ownership is checked explicitly here.
        var userId = currentUser.UserId ?? "";
        var brandTokens = includeBrand ? ProductVariants.BrandTokens(q) : [];
        return await WithWordSimilarityAsync(minScore, indexed => db.Database.SqlQuery<FoodMatch>($"""
            SELECT f."Id", f."Name", f."NameBg", f."Brand", f."KcalPer100g", f."ProteinPer100g", f."CarbsPer100g", f."FatPer100g",
                   f."FiberPer100g", f."SaturatedFatPer100g", f."DefaultServingGrams", f."OwnerUserId" IS NOT NULL AS "Own",
                   (f."Source" = 'Manual' AND f."OwnerUserId" IS NULL AND f."AddedByUserId" IS NOT NULL AND f."AddedByUserId" <> {userId}) AS "ByOther",
                   score.value::float8 AS "Score",
                   f."PieceGrams", f."PackGrams", f."ValuesEstimated", f."Unit" = 'Millilitres' AS "PerMillilitre"
            FROM "FoodItems" f
            CROSS JOIN LATERAL (SELECT GREATEST(
                word_similarity({q}, f."Name"), word_similarity({q}, COALESCE(f."NameBg", '')),
                CASE WHEN {includeBrand} AND word_similarity(f."Brand", {q}) = 1 THEN GREATEST(
                    word_similarity({q}, f."Brand" || ' ' || f."Name"), word_similarity({q}, f."Name" || ' ' || f."Brand"),
                    word_similarity({q}, f."Brand" || ' ' || f."NameBg"), word_similarity({q}, f."NameBg" || ' ' || f."Brand")) END
            ) AS value) score
            WHERE (f."OwnerUserId" IS NULL OR f."OwnerUserId" = {userId})
              AND (NOT {indexed} OR f."Name" %> {q} OR f."NameBg" %> {q} OR ({includeBrand} AND f."Brand" %> ANY ({brandTokens})))
              AND score.value >= {minScore}
            ORDER BY "Own" DESC, "Score" DESC, length(f."Name")
            LIMIT {take}
            """).ToListAsync(ct), ct);
    }

    /// <summary>The food's name as shown (Bulgarian when the reader uses it), or null when it isn't visible to this user.</summary>
    public async Task<string?> DisplayNameOrNullAsync(int id, CancellationToken ct) =>
        await FindVisibleAsync(id, ct) is { } item ? Localization.FoodNames.DisplayName(item) : null;

    /// <summary>Those of these foods that count in ml (SPEC 4).</summary>
    public async Task<IReadOnlySet<int>> MillilitreFoodsAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0 ? new HashSet<int>()
            : (await db.FoodItems.Where(f => list.Contains(f.Id) && f.Unit == FoodUnit.Millilitres).Select(f => f.Id).ToListAsync(ct)).ToHashSet();
    }

    /// <summary>Those of these foods that are the account's recipes (SPEC 5.12).</summary>
    public async Task<HashSet<int>> RecipeIdsAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0 ? [] : (await db.FoodItems.Where(f => list.Contains(f.Id) && f.Source == FoodSource.Recipe).Select(f => f.Id).ToListAsync(ct)).ToHashSet();
    }

    /// <summary>The food when this user may see it (their own or a shared one), otherwise null.</summary>
    public Task<FoodItem?> FindVisibleAsync(int id, CancellationToken ct) =>
        db.FoodItems.SingleOrDefaultAsync(f => f.Id == id, ct);

    /// <summary>
    /// A shared manual food another account added, deleted accounts included (SPEC 7). Says nothing about who; an
    /// unknown creator (older foods nobody logged) claims nothing.
    /// </summary>
    public static bool AddedByAnother(FoodItem item, string? userId) =>
        item.Source == FoodSource.Manual && item.OwnerUserId is null && item.AddedByUserId is { } addedBy && addedBy != userId;

    /// <summary>
    /// The Search tab's ranking (SPEC 7 Food): the user's own foods first, then names that start with what was typed
    /// (which also catches plurals: "banana" starts "Bananas, raw"), then the closest by similarity, shorter names
    /// first. The same food from two sources is listed once. The matcher keeps its own ranking (<see cref="SearchAsync"/>).
    /// </summary>
    public async Task<List<FoodMatch>> SearchForPickerAsync(string query, int take, CancellationToken ct)
    {
        var q = query.Trim();
        if (q.Length < 2)
        {
            return [];
        }

        var found = await PickAsync(q, take, ct);
        // Bulgarian typed in Latin letters ("boza") meets no Cyrillic-only product name, only look-alikes ("Borage, raw").
        // When no name starts with what was typed, its Cyrillic reading is searched too (QA 2026-11-24 M4), and its
        // results go first if one of their names starts with it. A search with such a name is left alone.
        if (!found.Any(StartsWithQuery) && Plate.Core.Text.BulgarianLatin.ToCyrillic(q) is { } reading)
        {
            var read = await PickAsync(reading, take, ct);
            found = (read.Any(StartsWithQuery) ? read.Concat(found) : found.Concat(read)).DistinctBy(f => f.Id).ToList();
        }

        // The reference tables repeat some USDA foods under the same name with slightly different numbers ("Bananas,
        // raw" three times); one is shown, the earliest stored, which is USDA's (seeded first). Own foods are kept.
        var keep = found
            .GroupBy(f => (f.Own, Name: f.Name.ToLowerInvariant(), Brand: f.Brand?.ToLowerInvariant()))
            .Select(g => g.MinBy(f => f.Id)!.Id)
            .ToHashSet();
        return found.Where(f => keep.Contains(f.Id)).Take(take).ToList();
    }

    /// <summary>The picker's score adds 1 for a name that starts with the search (see the query below).</summary>
    private static bool StartsWithQuery(FoodMatch f) => f.Score >= 1;

    private async Task<List<FoodMatch>> PickAsync(string q, int take, CancellationToken ct)
    {
        var userId = currentUser.UserId ?? "";
        var lower = q.ToLowerInvariant();
        return await WithWordSimilarityAsync(0.3, indexed => db.Database.SqlQuery<FoodMatch>($"""
            WITH m AS (
                SELECT f."Id", f."Name", f."NameBg", f."Brand", f."KcalPer100g", f."ProteinPer100g", f."CarbsPer100g", f."FatPer100g",
                       f."FiberPer100g", f."SaturatedFatPer100g", f."DefaultServingGrams", f."OwnerUserId" IS NOT NULL AS "Own",
                       (f."Source" = 'Manual' AND f."OwnerUserId" IS NULL AND f."AddedByUserId" IS NOT NULL AND f."AddedByUserId" <> {userId}) AS "ByOther",
                       f."PieceGrams", f."PackGrams", f."ValuesEstimated", f."Unit" = 'Millilitres' AS "PerMillilitre",
                       GREATEST(word_similarity({q}, f."Name"), word_similarity({q}, COALESCE(f."NameBg", ''))) AS sim,
                       starts_with(lower(f."Name"), {lower}) OR starts_with(lower(COALESCE(f."NameBg", '')), {lower}) AS pre
                FROM "FoodItems" f
                WHERE (f."OwnerUserId" IS NULL OR f."OwnerUserId" = {userId})
                  AND (NOT {indexed} OR f."Name" %> {q} OR f."NameBg" %> {q})
                  AND GREATEST(word_similarity({q}, f."Name"), word_similarity({q}, COALESCE(f."NameBg", ''))) >= 0.3
            )
            SELECT "Id", "Name", "NameBg", "Brand", "KcalPer100g", "ProteinPer100g", "CarbsPer100g", "FatPer100g",
                   "FiberPer100g", "SaturatedFatPer100g", "DefaultServingGrams", "Own", "ByOther", (sim + CASE WHEN pre THEN 1 ELSE 0 END)::float8 AS "Score",
                   "PieceGrams", "PackGrams", "ValuesEstimated", "PerMillilitre"
            FROM m
            -- Among names that start with the search, the shortest is the plain food ("Bananas, raw" before
            -- "Bananas, dehydrated, or banana powder"); the rest by similarity.
            ORDER BY "Own" DESC, pre DESC, CASE WHEN pre THEN length("Name") END, sim DESC, length("Name")
            LIMIT {take * 2}
            """).ToListAsync(ct), ct);
    }

    private async Task<List<FoodMatch>> WithWordSimilarityAsync(double minScore, Func<bool, Task<List<FoodMatch>>> read, CancellationToken ct)
    {
        // A zero/non-finite threshold needs the original unfiltered candidate set (including score zero).
        if (minScore <= 0 || minScore > 1 || !double.IsFinite(minScore)) return await read(false);

        var existing = db.Database.CurrentTransaction;
        await using var owned = existing is null ? await db.Database.BeginTransactionAsync(ct) : null;
        if (existing is not null) await existing.CreateSavepointAsync("FoodSearchThreshold", ct);
        try
        {
            // pg_trgm's operator threshold is float4. Use a slightly looser indexed predicate, then keep the
            // original float8 >= comparison above, including exact boundary matches. SET LOCAL cannot leak
            // into a pooled connection or alter another search's threshold.
            var threshold = MathF.Max(0, MathF.BitDecrement((float)minScore)).ToString("R", CultureInfo.InvariantCulture);
            await db.Database.ExecuteSqlAsync($"SELECT set_config('pg_trgm.word_similarity_threshold', {threshold}, true)", ct);
            return await read(true);
        }
        finally
        {
            if (existing is not null)
            {
                // This scope only reads. Rolling back its savepoint also restores the caller's setting,
                // even when a cancelled/failed query left the transaction in an error state.
                await existing.RollbackToSavepointAsync("FoodSearchThreshold", CancellationToken.None);
                await existing.ReleaseSavepointAsync("FoodSearchThreshold", CancellationToken.None);
            }
        }
    }

    public async Task<FoodItem> GetAsync(int id, CancellationToken ct) =>
        await db.FoodItems.SingleOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException("Food not found.");

    /// <summary>The user's own food with this barcode, else a shared one. Never asks Open Food Facts.</summary>
    public async Task<FoodItem?> FindByBarcodeAsync(string barcode, CancellationToken ct) =>
        // The query filter hides other users' private foods (SPEC 4).
        await db.FoodItems.Where(f => f.Barcode == barcode).OrderBy(f => f.OwnerUserId == null).FirstOrDefaultAsync(ct);

    /// <summary>The cached food for a barcode, else an Open Food Facts lookup that is cached as a shared food.</summary>
    public async Task<BarcodeResult> LookupBarcodeAsync(string barcode, CancellationToken ct)
    {
        if (!Barcodes.IsValid(barcode))
        {
            throw new ValidationException("That isn't a valid EAN or UPC barcode.");
        }

        // The user's own food first, then a shared one (SPEC 4).
        var cached = await FindByBarcodeAsync(barcode, ct);
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

    /// <summary>
    /// Names a shared product Open Food Facts had no name for. Only the first name counts: the update is conditional,
    /// so a second user (or a stale page) leaves it as it is. The user's own entries still carrying the stand-in name
    /// get the new one.
    /// </summary>
    public async Task<FoodItem> NameUnnamedAsync(FoodItem item, string name, CancellationToken ct)
    {
        name = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (!item.Unnamed || name.Length == 0)
        {
            return item;
        }

        // Short enough that the entry's "name (brand)" stays within its 200 characters.
        name = name.Length > MaxGivenName ? name[..MaxGivenName] : name;
        var named = await db.FoodItems.Where(f => f.Id == item.Id && f.Unnamed)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Name, name).SetProperty(f => f.Unnamed, false), ct);
        if (named == 1)
        {
            var old = EntryDescription(item.Name, item.Brand);
            await db.FoodLog.Where(e => e.FoodItemId == item.Id && e.Description == old)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Description, Clip(EntryDescription(name, item.Brand))), ct);
        }

        await db.Entry(item).ReloadAsync(ct);
        return item;
    }

    public const int MaxGivenName = 100;

    /// <summary>How a logged packaged food is described: its name, then the brand in brackets unless the name starts with it.</summary>
    public static string EntryDescription(string name, string? brand) =>
        brand is null || name.StartsWith(brand, StringComparison.OrdinalIgnoreCase) ? name : $"{name} ({brand})";

    public Task<ProductSearch> SearchOpenFoodFactsAsync(string query, CancellationToken ct) =>
        query.Trim().Length < 2 ? Task.FromResult(new ProductSearch(LookupStatus.NotFound, [])) : foodDatabase.SearchAsync(query.Trim(), ct);

    /// <summary>Stores an Open Food Facts product as a shared food so repeat scans need no API call.</summary>
    public async Task<FoodItem> CacheAsync(ProductInfo p, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(p.Barcode) && await db.FoodItems.IgnoreQueryFilters().SingleOrDefaultAsync(f => f.Barcode == p.Barcode && f.OwnerUserId == null, ct) is { } existing)
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
            PieceGrams = p.PieceGrams,
            PackGrams = p.PackGrams,
            Source = FoodSource.OpenFoodFacts,
            Unnamed = p.Unnamed,
        };
        db.FoodItems.Add(item);
        await db.SaveChangesAsync(ct);
        return item;
    }

    /// <summary>
    /// A food typed in by hand. With a barcode and <paramref name="share"/> it is shared, so others who scan the same
    /// product get it; otherwise it belongs to this user only (SPEC 4).
    /// </summary>
    public async Task<FoodItem> CreateManualAsync(string? name, string? barcode, Per100g per100, decimal? servingGrams, bool share, CancellationToken ct,
        FoodUnit unit = FoodUnit.Grams)
    {
        if (!Enum.IsDefined(unit))
        {
            throw new ValidationException("This food form is out of date. Check whether it was saved, then reload before trying again.");
        }

        name = name?.Trim() ?? "";
        if (name.Length is 0 or > 200)
        {
            throw new ValidationException("Name must be 1 to 200 characters.", nameof(name));
        }

        if (FoodMath.Validate(servingGrams ?? 100, per100) is { } error)
        {
            throw new ValidationException(error);
        }

        barcode = string.IsNullOrWhiteSpace(barcode) ? null : Barcodes.Normalize(barcode);
        if (barcode is not null && !Barcodes.IsValid(barcode))
        {
            throw new ValidationException("That isn't a valid EAN or UPC barcode.");
        }

        var userId = currentUser.UserId ?? throw new InvalidOperationException("No signed-in user.");
        var shared = barcode is not null && share;
        var taken = new ValidationException(shared
            ? "A food with that barcode is already shared. Scan it again to use it."
            : "You already have a food with that barcode.");
        if (barcode is not null && await db.FoodItems.IgnoreQueryFilters().AnyAsync(
                f => f.Barcode == barcode && (shared ? f.OwnerUserId == null : f.OwnerUserId == userId), ct))
        {
            throw taken;
        }

        var item = new FoodItem
        {
            OwnerUserId = shared ? null : userId,
            AddedByUserId = userId,
            Name = name,
            Barcode = barcode,
            KcalPer100g = per100.Kcal,
            ProteinPer100g = per100.Protein,
            CarbsPer100g = per100.Carbs,
            FatPer100g = per100.Fat,
            FiberPer100g = per100.Fiber,
            SaturatedFatPer100g = per100.SaturatedFat,
            DefaultServingGrams = servingGrams,
            Unit = unit,
            Source = FoodSource.Manual,
        };
        db.FoodItems.Add(item);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // Someone saved the same barcode between the check above and this insert.
            db.Entry(item).State = EntityState.Detached;
            throw taken;
        }

        return item;
    }

    private static string Clip(string s) => s.Length > 200 ? s[..200] : s;
}
