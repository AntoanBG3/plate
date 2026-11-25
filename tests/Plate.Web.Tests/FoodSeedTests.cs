using FluentAssertions;
using Plate.Core.Domain;
using Plate.Web.Data.Seed;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Plate.Web.Tests;

public class FoodSeedTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
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
    public async Task A_private_food_with_a_seed_barcode_neither_blocks_the_shared_product_nor_gets_its_values()
    {
        // Private barcodes can repeat a shared one since Phase 13; the seed only looks at shared foods.
        var tag = Guid.NewGuid().ToString("N")[..8];
        var barcode = "38" + Random.Shared.NextInt64(10_000_000_000, 99_999_999_999);
        var userId = await fixture.CreateUserAsync();
        await using (var db = fixture.CreateContext(userId))
        {
            db.FoodItems.Add(new FoodItem { Name = $"My yogurt {tag}", OwnerUserId = userId, AddedByUserId = userId, Barcode = barcode, KcalPer100g = 60, Source = FoodSource.Manual });
            await db.SaveChangesAsync();
        }

        var csv = TempCsv(
            "kind,barcode,name_bg,name_en,brand,kcal,protein,carbs,fat,serving_g,source,fiber,satfat",
            $"product,{barcode},Кисело мляко {tag},,Верея,62,3.3,4.2,3.6,400,https://world.openfoodfacts.org/product/{barcode},0.5,2.3");

        await using (var db = fixture.CreateContext(null))
        {
            await BgFoodSeed.SeedAsync(db, csv, default);
        }

        await using var check = fixture.CreateContext(null);
        var rows = await check.FoodItems.IgnoreQueryFilters().Where(f => f.Barcode == barcode).ToListAsync();
        rows.Should().ContainSingle(f => f.OwnerUserId == null && f.Source == FoodSource.OpenFoodFacts && f.FiberPer100g == 0.5m);
        rows.Single(f => f.OwnerUserId == userId).Should().Match<FoodItem>(f => f.FiberPer100g == null && f.SaturatedFatPer100g == null);
    }

    [Fact]
    public async Task Piece_weights_and_estimated_values_are_imported_and_filled_in_on_foods_seeded_before()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using (var db = fixture.CreateContext(null))
        {
            // Seeded by a file without the two columns: the serving was there, the piece weight and basis were not.
            db.FoodItems.Add(new FoodItem { Name = $"Pastry {tag}", NameBg = $"Закуска {tag}, Пекарна Лидл", Source = FoodSource.Reference, KcalPer100g = 233, DefaultServingGrams = 90 });
            await db.SaveChangesAsync();
        }

        var csv = TempCsv(
            "kind,barcode,name_bg,name_en,brand,kcal,protein,carbs,fat,serving_g,source,fiber,satfat,piece_g,basis",
            $"generic,,\"Закуска {tag}, Пекарна Лидл\",Pastry {tag},Lidl,233,7.6,23.3,11.9,90,https://example.test (estimate),1.3,6.1,90,estimate",
            $"generic,,\"Кифла {tag}, Пекарна Лидл\",Roll {tag},Lidl,260,8,50,2,60,https://example.test,,,60,label",
            $"generic,,Хляб {tag},Bread {tag},,250,8,50,1,,https://example.test,,,,");
        await using (var db = fixture.CreateContext(null))
        {
            await BgFoodSeed.SeedAsync(db, csv, default);
            await BgFoodSeed.SeedAsync(db, csv, default);
        }

        await using var check = fixture.CreateContext(null);
        var foods = await check.FoodItems.IgnoreQueryFilters().Where(f => f.Name.EndsWith(tag)).ToDictionaryAsync(f => f.Name);
        foods.Should().HaveCount(3);
        foods[$"Pastry {tag}"].Should().Match<FoodItem>(f => f.PieceGrams == 90 && f.ValuesEstimated);
        foods[$"Roll {tag}"].Should().Match<FoodItem>(f => f.PieceGrams == 60 && !f.ValuesEstimated && f.DefaultServingGrams == 60);
        foods[$"Bread {tag}"].Should().Match<FoodItem>(f => f.PieceGrams == null && !f.ValuesEstimated);
    }

    [Fact]
    public async Task Reference_foods_still_at_their_old_values_are_corrected_and_others_left_alone()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using (var db = fixture.CreateContext(null))
        {
            db.FoodItems.AddRange(
                new FoodItem { Name = "Strawberries, raw", NameBg = $"Ягоди {tag}", Source = FoodSource.Reference, KcalPer100g = 79, ProteinPer100g = 0.9m, CarbsPer100g = 17.4m, FatPer100g = 0.5m },
                new FoodItem { Name = "Mustard, prepared, yellow", NameBg = $"Горчица {tag}", Source = FoodSource.Reference, KcalPer100g = 148, ProteinPer100g = 6.8m, CarbsPer100g = 11.7m, FatPer100g = 8.2m },
                new FoodItem { Name = "Hand fixed", NameBg = $"Ягоди {tag}", Source = FoodSource.Reference, KcalPer100g = 33, ProteinPer100g = 0.7m, CarbsPer100g = 7.7m, FatPer100g = 0.3m });
            await db.SaveChangesAsync();
        }

        var fixes = TempCsv(
            "name_bg,old_kcal,name_en,kcal,protein,carbs,fat,fiber,satfat",
            $"Ягоди {tag},79,\"Strawberries, raw\",31,0.64,7.63,0.22,1.8,0.0",
            $"Горчица {tag},148,,148,6.8,11.7,8.2,,");
        await using (var db = fixture.CreateContext(null))
        {
            await DataFixups.RunAsync(db, default, fixes);
            await DataFixups.RunAsync(db, default, fixes); // a second start finds nothing to change
        }

        await using var check = fixture.CreateContext(null);
        var rows = await check.FoodItems.IgnoreQueryFilters().Where(f => f.NameBg!.EndsWith(tag)).ToListAsync();
        rows.Should().ContainSingle(f => f.Name == "Strawberries, raw").Which.Should().Match<FoodItem>(f => f.KcalPer100g == 31 && f.FiberPer100g == 1.8m && f.CarbsPer100g == 7.63m);
        rows.Should().ContainSingle(f => f.Name == "Hand fixed" && f.KcalPer100g == 33, "a food corrected since is not at its old energy");
        rows.Single(f => f.NameBg == $"Горчица {tag}").Name.Should().Be($"Горчица {tag}", "a product goes by its Bulgarian name once unlinked");
    }

    [Fact]
    public async Task A_retired_product_still_at_its_old_energy_is_removed_and_its_entries_keep_their_totals()
    {
        var userId = await fixture.CreateUserAsync();
        var (bad, corrected) = ("38" + Random.Shared.NextInt64(10_000_000_000, 99_999_999_999), "38" + Random.Shared.NextInt64(10_000_000_000, 99_999_999_999));
        await using (var db = fixture.CreateContext(null))
        {
            var muesli = new FoodItem { Name = "Мюсли", NameBg = "Мюсли", Barcode = bad, Source = FoodSource.OpenFoodFacts, KcalPer100g = 134, ProteinPer100g = 5, CarbsPer100g = 15, FatPer100g = 5.4m };
            db.FoodItems.AddRange(muesli,
                new FoodItem { Name = "Мюсли", NameBg = "Мюсли", Barcode = corrected, Source = FoodSource.OpenFoodFacts, KcalPer100g = 372, ProteinPer100g = 9, CarbsPer100g = 75, FatPer100g = 4 });
            db.FoodLog.Add(new FoodLogEntry { UserId = userId, Date = new DateOnly(2026, 11, 22), Description = "Мюсли", Grams = 50, Kcal = 67, ProteinG = 2.5m, FoodItem = muesli, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var retired = TempCsv("barcode,kcal,reason", $"{bad},134,per-portion values", $"{corrected},134,a row fixed since keeps its values");
        await using (var db = fixture.CreateContext(null))
        {
            await DataFixups.RunAsync(db, default, retiredProducts: retired);
            await DataFixups.RunAsync(db, default, retiredProducts: retired); // a second start finds nothing to change
        }

        await using var check = fixture.CreateContext(null);
        (await check.FoodItems.IgnoreQueryFilters().AnyAsync(f => f.Barcode == bad)).Should().BeFalse();
        (await check.FoodItems.IgnoreQueryFilters().AnyAsync(f => f.Barcode == corrected)).Should().BeTrue("it isn't at the retired energy");
        var entry = await check.FoodLog.IgnoreQueryFilters().SingleAsync(e => e.UserId == userId);
        (entry.FoodItemId, entry.Kcal, entry.Grams).Should().Be(((int?)null, 67m, 50m));
    }

    [Fact]
    public async Task A_missing_piece_weight_is_filled_by_USDA_name_or_barcode_and_one_set_by_hand_stays()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var (barcode, kept) = ("38" + Random.Shared.NextInt64(10_000_000_000, 99_999_999_999), "38" + Random.Shared.NextInt64(10_000_000_000, 99_999_999_999));
        await using (var db = fixture.CreateContext(null))
        {
            db.FoodItems.AddRange(
                new FoodItem { Name = $"Bananas, raw {tag}", Source = FoodSource.Usda, KcalPer100g = 89, ProteinPer100g = 1, CarbsPer100g = 23, FatPer100g = 0.3m },
                new FoodItem { Name = "Кроасан", NameBg = "Кроасан", Barcode = barcode, Source = FoodSource.OpenFoodFacts, KcalPer100g = 450, ProteinPer100g = 6, CarbsPer100g = 50, FatPer100g = 25 },
                new FoodItem { Name = "Кроасан", NameBg = "Кроасан", Barcode = kept, Source = FoodSource.OpenFoodFacts, KcalPer100g = 450, ProteinPer100g = 6, CarbsPer100g = 50, FatPer100g = 25, PieceGrams = 60 });
            await db.SaveChangesAsync();
        }

        var weights = TempCsv("match,key,piece_g,source", $"usda,\"Bananas, raw {tag}\",118,USDA", $"barcode,{barcode},92,OFF", $"barcode,{kept},92,OFF");
        await using (var db = fixture.CreateContext(null))
        {
            await DataFixups.RunAsync(db, default, pieceWeights: weights);
            await DataFixups.RunAsync(db, default, pieceWeights: weights); // a second start finds nothing to change
        }

        await using var check = fixture.CreateContext(null);
        (await check.FoodItems.IgnoreQueryFilters().SingleAsync(f => f.Name == $"Bananas, raw {tag}")).PieceGrams.Should().Be(118);
        (await check.FoodItems.IgnoreQueryFilters().SingleAsync(f => f.Barcode == barcode)).PieceGrams.Should().Be(92);
        (await check.FoodItems.IgnoreQueryFilters().SingleAsync(f => f.Barcode == kept)).PieceGrams.Should().Be(60, "a weight already set stays");
    }

    [Fact]
    public void Every_piece_weight_names_a_seeded_food_and_its_source()
    {
        var usda = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "Seed", "usda-foods.csv"));
        var bg = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "Seed", "bg-foods.csv"));
        foreach (var line in File.ReadLines(DataFixups.PieceWeightsPath).Skip(1).Where(l => l.Length > 0))
        {
            var f = UsdaSeed.ParseCsvLine(line);
            (f[0] == "usda" ? usda.Contains($"\"{f[1]}\"") || usda.Contains("," + f[1] + ",") : bg.Contains("," + f[1] + ",")).Should().BeTrue(line);
            decimal.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture).Should().BeInRange(5, 500, line);
            f[3].Should().NotBeNullOrWhiteSpace(line);
        }
    }

    [Fact]
    public void The_retired_products_are_not_in_the_seed()
    {
        var seed = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "Seed", "bg-foods.csv"));
        foreach (var line in File.ReadLines(DataFixups.RetiredProductsPath).Skip(1))
        {
            seed.Should().NotContain("product," + line.Split(',')[0] + ",");
        }
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

    // A Latin letter inside a Cyrillic word or the reverse (or a garbled word like "буrelation") in any food name.
    [Theory]
    [MemberData(nameof(Files))]
    public void No_food_name_mixes_Latin_and_Cyrillic_letters(string path, int firstMacroColumn)
    {
        if (!File.Exists(path))
        {
            return;
        }

        File.ReadLines(path).Skip(1)
            .Select(UsdaSeed.ParseCsvLine)
            .SelectMany(f => f.Take(firstMacroColumn).Concat(f.Skip(firstMacroColumn + 4).Where(v => !decimal.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))))
            .Where(Plate.Core.Text.MixedScript.HasMixedWord)
            .Should().BeEmpty();
    }
}
