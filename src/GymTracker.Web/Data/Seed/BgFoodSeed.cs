using System.Globalization;
using GymTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Data.Seed;

/// <summary>
/// Imports Data/Seed/bg-foods.csv (built by tools/foods): Bulgarian generic foods and dishes compiled from public
/// tables, and products sold in Bulgaria from Open Food Facts. Runs on every start and only adds what is missing,
/// so a newer file adds its new rows: products by barcode, generic foods by Bulgarian name.
/// </summary>
public static class BgFoodSeed
{
    public static readonly string DefaultPath = Path.Combine(AppContext.BaseDirectory, "Data", "Seed", "bg-foods.csv");

    public static async Task SeedAsync(AppDbContext db, string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var foods = db.FoodItems.IgnoreQueryFilters();
        var barcodes = (await foods.Where(f => f.Barcode != null).Select(f => f.Barcode!).ToListAsync(ct)).ToHashSet();
        var references = (await foods.Where(f => f.Source == FoodSource.Reference && f.NameBg != null).Select(f => f.NameBg!).ToListAsync(ct)).ToHashSet();

        var items = new List<FoodItem>();
        var extras = new Dictionary<string, (decimal? Fiber, decimal? Saturated)>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            // kind,barcode,name_bg,name_en,brand,kcal,protein,carbs,fat,serving_g,source,fiber,satfat
            var f = UsdaSeed.ParseCsvLine(line);
            if (f.Count < 10 || f[2].Length == 0)
            {
                continue;
            }

            var product = f[0] == "product";
            var (fiber, saturated) = (Optional(f, 11), Optional(f, 12));
            if (product && (fiber is not null || saturated is not null))
            {
                extras.TryAdd(f[1], (fiber, saturated));
            }

            if (product ? !barcodes.Add(f[1]) : !references.Add(f[2]))
            {
                continue;
            }

            items.Add(new FoodItem
            {
                Name = Cut(product || f[3].Length == 0 ? f[2] : f[3]),
                NameBg = Cut(f[2]),
                Brand = f[4].Length == 0 ? null : Cut(f[4]),
                Barcode = product ? f[1] : null,
                KcalPer100g = D(f[5]),
                ProteinPer100g = D(f[6]),
                CarbsPer100g = D(f[7]),
                FatPer100g = D(f[8]),
                FiberPer100g = fiber,
                SaturatedFatPer100g = saturated,
                DefaultServingGrams = f[9].Length == 0 ? null : D(f[9]),
                Source = product ? FoodSource.OpenFoodFacts : FoodSource.Reference,
            });
        }

        db.ChangeTracker.AutoDetectChangesEnabled = false;
        foreach (var chunk in items.Chunk(1000))
        {
            db.FoodItems.AddRange(chunk);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        db.ChangeTracker.AutoDetectChangesEnabled = true;
        await AddFiberAndSaturatedFatAsync(db, extras, ct);
    }

    /// <summary>Products seeded before the CSV had fiber and saturated fat get them, by barcode, where both are still unknown.</summary>
    private static async Task AddFiberAndSaturatedFatAsync(AppDbContext db, Dictionary<string, (decimal? Fiber, decimal? Saturated)> extras, CancellationToken ct)
    {
        var codes = extras.Keys.ToList();
        var missing = await db.FoodItems.IgnoreQueryFilters()
            .Where(f => f.Barcode != null && codes.Contains(f.Barcode) && f.FiberPer100g == null && f.SaturatedFatPer100g == null)
            .ToListAsync(ct);
        foreach (var item in missing)
        {
            (item.FiberPer100g, item.SaturatedFatPer100g) = extras[item.Barcode!];
        }

        await db.SaveChangesAsync(ct);
    }

    private static decimal? Optional(List<string> fields, int index) =>
        fields.Count > index && fields[index].Length > 0 ? D(fields[index]) : null;

    private static string Cut(string s) => s.Length > 200 ? s[..200] : s;

    private static decimal D(string s) => decimal.Round(decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture), 2);
}
