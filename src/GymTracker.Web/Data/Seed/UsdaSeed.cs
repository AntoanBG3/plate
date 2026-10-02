using System.Globalization;
using System.Text;
using GymTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Data.Seed;

/// <summary>
/// Imports the USDA FoodData Central subset (built by tools/usda/build_usda_subset.py) as shared foods,
/// once, when no USDA foods exist yet.
/// </summary>
public static class UsdaSeed
{
    public static readonly string DefaultPath = Path.Combine(AppContext.BaseDirectory, "Data", "Seed", "usda-foods.csv");

    public static async Task SeedAsync(AppDbContext db, string path, CancellationToken ct)
    {
        if (!File.Exists(path) || await db.FoodItems.IgnoreQueryFilters().AnyAsync(f => f.Source == FoodSource.Usda, ct))
        {
            return;
        }

        var items = new List<FoodItem>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var f = ParseCsvLine(line);
            if (f.Count < 6)
            {
                continue;
            }

            items.Add(new FoodItem
            {
                Name = f[1].Length > 200 ? f[1][..200] : f[1],
                KcalPer100g = D(f[2]),
                ProteinPer100g = D(f[3]),
                CarbsPer100g = D(f[4]),
                FatPer100g = D(f[5]),
                Source = FoodSource.Usda,
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

    private static decimal D(string s) => decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Splits one RFC 4180 line: commas, double-quoted fields, doubled quotes inside them.</summary>
    internal static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return fields;
    }
}
