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
        if (!File.Exists(path))
        {
            return;
        }

        if (await db.FoodItems.IgnoreQueryFilters().AnyAsync(f => f.Source == FoodSource.Usda, ct))
        {
            await AddBulgarianNamesAsync(db, path, ct);
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
                Name = Cut(f[1]),
                KcalPer100g = D(f[2]),
                ProteinPer100g = D(f[3]),
                CarbsPer100g = D(f[4]),
                FatPer100g = D(f[5]),
                NameBg = f.Count > 6 && f[6].Length > 0 ? Cut(f[6]) : null,
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

    /// <summary>
    /// Databases seeded before the CSV had Bulgarian names get them once: USDA rows without one are matched by
    /// their English name. Rows that already have a Bulgarian name are left alone.
    /// </summary>
    private static async Task AddBulgarianNamesAsync(AppDbContext db, string path, CancellationToken ct)
    {
        var missing = await db.FoodItems.IgnoreQueryFilters().Where(f => f.Source == FoodSource.Usda && f.NameBg == null).ToListAsync(ct);
        if (missing.Count == 0)
        {
            return;
        }

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var f = ParseCsvLine(line);
            if (f.Count > 6 && f[6].Length > 0)
            {
                names.TryAdd(Cut(f[1]), Cut(f[6]));
            }
        }

        if (names.Count == 0)
        {
            return;
        }

        foreach (var item in missing)
        {
            if (names.TryGetValue(item.Name, out var bg))
            {
                item.NameBg = bg;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static string Cut(string s) => s.Length > 200 ? s[..200] : s;

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
