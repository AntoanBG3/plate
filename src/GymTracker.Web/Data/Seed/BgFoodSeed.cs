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
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            // kind,barcode,name_bg,name_en,brand,kcal,protein,carbs,fat,serving_g,source
            var f = UsdaSeed.ParseCsvLine(line);
            if (f.Count < 10 || f[2].Length == 0)
            {
                continue;
            }

            var product = f[0] == "product";
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
    }

    private static string Cut(string s) => s.Length > 200 ? s[..200] : s;

    private static decimal D(string s) => decimal.Round(decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture), 2);
}
