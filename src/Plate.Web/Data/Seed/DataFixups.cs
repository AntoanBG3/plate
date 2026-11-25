using Plate.Core.Domain;
using Plate.Core.Text;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Data.Seed;

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

    /// <summary>Corrections to Bulgarian reference foods from the 2026-11-21 audit (reports/reference-foods-audit.md).</summary>
    public static readonly string ReferenceFixesPath = Path.Combine(AppContext.BaseDirectory, "Data", "Seed", "reference-fixes.csv");

    /// <summary>Open Food Facts products whose values were wrong enough to retire (2026-11-24 QA).</summary>
    public static readonly string RetiredProductsPath = Path.Combine(AppContext.BaseDirectory, "Data", "Seed", "retired-products.csv");

    /// <summary>Piece weights for shared USDA foods and products that had none (2026-11-24 QA, L5).</summary>
    public static readonly string PieceWeightsPath = Path.Combine(AppContext.BaseDirectory, "Data", "Seed", "piece-weights.csv");

    public static async Task RunAsync(AppDbContext db, CancellationToken ct, string? referenceFixes = null, string? retiredProducts = null, string? pieceWeights = null)
    {
        await FixReferenceFoodsAsync(db, referenceFixes ?? ReferenceFixesPath, ct);
        await RetireProductsAsync(db, retiredProducts ?? RetiredProductsPath, ct);
        await AddPieceWeightsAsync(db, pieceWeights ?? PieceWeightsPath, ct);

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

    /// <summary>
    /// Shared Open Food Facts products whose values are wrong for what they are, listed by barcode with the energy they
    /// were cached with: a 134 kcal "Мюсли" stood for every typed muesli, since same-named products go in as one food.
    /// Only a row still at that energy is removed, so one corrected since stays. Logged entries keep the totals they were
    /// saved with (their link is cleared), and words remembered for the product are forgotten. The seed no longer has
    /// them, so a later start doesn't add them back; a scan of the barcode asks Open Food Facts again.
    /// </summary>
    private static async Task RetireProductsAsync(AppDbContext db, string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var line in File.ReadLines(path).Skip(1).Where(l => l.Trim().Length > 0))
        {
            // barcode,kcal,reason
            var f = UsdaSeed.ParseCsvLine(line);
            var (barcode, kcal) = (f[0], decimal.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture));
            await db.FoodItems.IgnoreQueryFilters()
                .Where(x => x.OwnerUserId == null && x.Source == FoodSource.OpenFoodFacts && x.Barcode == barcode && x.KcalPer100g == kcal)
                .ExecuteDeleteAsync(ct);
        }
    }

    /// <summary>
    /// Piece weights for foods counted in pieces that had none, so a Snickers or a banana from Search starts at one piece,
    /// not 100 g (QA 2026-11-24 L5). Each row names a shared USDA food by its English name ("usda") or a product by its
    /// barcode ("barcode"), with its source. Only a missing weight is filled, so one set by hand stays and a start after
    /// the fix writes nothing.
    /// </summary>
    private static async Task AddPieceWeightsAsync(AppDbContext db, string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var line in File.ReadLines(path).Skip(1).Where(l => l.Trim().Length > 0))
        {
            // match,key,piece_g,source
            var f = UsdaSeed.ParseCsvLine(line);
            var (match, key, piece) = (f[0], f[1], decimal.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture));
            var foods = db.FoodItems.IgnoreQueryFilters().Where(x => x.OwnerUserId == null && x.PieceGrams == null);
            foods = match == "usda"
                ? foods.Where(x => x.Source == FoodSource.Usda && x.Name == key)
                : foods.Where(x => x.Source == FoodSource.OpenFoodFacts && x.Barcode == key);
            await foods.ExecuteUpdateAsync(s => s.SetProperty(x => x.PieceGrams, piece), ct);
        }
    }

    /// <summary>
    /// Shared reference foods whose values or English link were wrong: each row of the fixes file names a food by its
    /// Bulgarian name and the energy it was seeded with, and gives the English name and values to use (an empty English
    /// name for a product, which then goes by its Bulgarian one). Only a row still at its old energy is changed, so a
    /// food corrected by hand since is left alone, and a start after the fix writes nothing. Logged entries keep the
    /// totals they were saved with.
    /// </summary>
    private static async Task FixReferenceFoodsAsync(AppDbContext db, string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return;
        }

        static decimal D(string s) => decimal.Parse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
        static decimal? Optional(string s) => s.Length == 0 ? null : D(s);
        foreach (var line in File.ReadLines(path).Skip(1).Where(l => l.Trim().Length > 0))
        {
            // name_bg,old_kcal,name_en,kcal,protein,carbs,fat,fiber,satfat
            var f = UsdaSeed.ParseCsvLine(line);
            var (nameBg, oldKcal) = (f[0], D(f[1]));
            var rows = await db.FoodItems.IgnoreQueryFilters()
                .Where(x => x.OwnerUserId == null && x.Source == FoodSource.Reference && x.NameBg == nameBg && x.KcalPer100g == oldKcal)
                .ToListAsync(ct);
            foreach (var food in rows)
            {
                food.Name = f[2].Length == 0 ? nameBg : f[2];
                food.KcalPer100g = D(f[3]);
                food.ProteinPer100g = D(f[4]);
                food.CarbsPer100g = D(f[5]);
                food.FatPer100g = D(f[6]);
                food.FiberPer100g = Optional(f.ElementAtOrDefault(7) ?? "");
                food.SaturatedFatPer100g = Optional(f.ElementAtOrDefault(8) ?? "");
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
