using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Web.Data.Seed;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class FoodSeedTests(PostgresFixture fixture)
{
    private static string TempCsv(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gym-seed-{Guid.NewGuid():N}.csv");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public async Task Bulgarian_foods_import_once_and_are_found_by_their_Bulgarian_name()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var barcode = "38" + Random.Shared.NextInt64(10_000_000_000, 99_999_999_999);
        var csv = TempCsv(
            "kind,barcode,name_bg,name_en,brand,kcal,protein,carbs,fat,serving_g,source",
            $"generic,,Баница {tag},Banitsa {tag},,233,11.1,21.4,11.4,,https://example.test/a https://example.test/b",
            $"product,{barcode},\"Кисело мляко {tag}, 3,6%\",,Верея,62,3.3,4.2,3.6,400,https://world.openfoodfacts.org/product/{barcode}");

        await using (var db = fixture.CreateContext(null))
        {
            await BgFoodSeed.SeedAsync(db, csv, default);
            await BgFoodSeed.SeedAsync(db, csv, default); // a second start adds nothing
        }

        await using var check = fixture.CreateContext(null);
        var generic = await check.FoodItems.IgnoreQueryFilters().Where(f => f.NameBg == $"Баница {tag}").ToListAsync();
        generic.Should().ContainSingle().Which.Should().Match<FoodItem>(f =>
            f.Name == $"Banitsa {tag}" && f.Source == FoodSource.Reference && f.OwnerUserId == null && f.Barcode == null && f.KcalPer100g == 233);
        var product = await check.FoodItems.IgnoreQueryFilters().SingleAsync(f => f.Barcode == barcode);
        product.Should().Match<FoodItem>(f => f.NameBg == $"Кисело мляко {tag}, 3,6%" && f.Brand == "Верея" && f.DefaultServingGrams == 400 && f.Source == FoodSource.OpenFoodFacts);

        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        (await s.Foods.SearchAsync($"баница {tag}", 5, 0.3, default)).Should().Contain(m => m.NameBg == $"Баница {tag}");
    }

    [Fact]
    public async Task USDA_foods_seeded_earlier_get_their_Bulgarian_names_once()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using (var db = fixture.CreateContext(null))
        {
            db.FoodItems.Add(new FoodItem { Name = $"Bananas, raw {tag}", Source = FoodSource.Usda, KcalPer100g = 89 });
            db.FoodItems.Add(new FoodItem { Name = $"Apples, raw {tag}", NameBg = "Already named", Source = FoodSource.Usda, KcalPer100g = 52 });
            await db.SaveChangesAsync();
        }

        var csv = TempCsv(
            "fdc_id,name,kcal,protein,carbs,fat,name_bg",
            $"1,\"Bananas, raw {tag}\",89.0,1.1,22.8,0.3,\"Банан, суров\"",
            $"2,\"Apples, raw {tag}\",52.0,0.3,13.8,0.2,\"Ябълка, сурова\"");
        await using (var db = fixture.CreateContext(null))
        {
            await UsdaSeed.SeedAsync(db, csv, default);
        }

        await using var check = fixture.CreateContext(null);
        (await check.FoodItems.IgnoreQueryFilters().SingleAsync(f => f.Name == $"Bananas, raw {tag}")).NameBg.Should().Be("Банан, суров");
        (await check.FoodItems.IgnoreQueryFilters().SingleAsync(f => f.Name == $"Apples, raw {tag}")).NameBg.Should().Be("Already named");
    }
}

/// <summary>Checks the seed files that ship with the app, so a broken data pipeline run fails the build.</summary>
public class ShippedSeedFileTests
{
    public static TheoryData<string, int> Files => new()
    {
        { UsdaSeed.DefaultPath, 2 },
        { BgFoodSeed.DefaultPath, 5 },
    };

    [Theory]
    [MemberData(nameof(Files))]
    public void Every_row_has_a_name_and_numeric_macros(string path, int firstMacroColumn)
    {
        if (!File.Exists(path))
        {
            return; // bg-foods.csv is optional
        }

        var bad = File.ReadLines(path).Skip(1)
            .Select(UsdaSeed.ParseCsvLine)
            .Where(f => f.Count <= firstMacroColumn + 3
                || f.Skip(firstMacroColumn).Take(4).Any(v => !decimal.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) || d < 0))
            .Select(f => string.Join(",", f))
            .Take(5)
            .ToList();

        bad.Should().BeEmpty();
        File.ReadLines(path).Skip(1).Should().NotBeEmpty();
    }
}
