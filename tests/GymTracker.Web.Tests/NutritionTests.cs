using System.Text.Json.Nodes;
using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Data.Seed;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Food;
using GymTracker.Web.Integrations;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class NutritionTests(PostgresFixture fixture)
{
    private static readonly DateOnly Day = new(2026, 10, 2);

    private static ParsedFoodItem Parsed(string name, decimal grams, FoodState state) =>
        new(name, name, grams, state, null, null, new Per100g(999, 0, 0, 0));

    [Fact]
    public async Task Usda_subset_is_imported_once()
    {
        await using var db = fixture.CreateContext(null);
        var count = await db.FoodItems.IgnoreQueryFilters().CountAsync(f => f.Source == FoodSource.Usda);
        count.Should().BeGreaterThan(7000);

        await UsdaSeed.SeedAsync(db, UsdaSeed.DefaultPath, default);
        (await db.FoodItems.IgnoreQueryFilters().CountAsync(f => f.Source == FoodSource.Usda)).Should().Be(count);
    }

    [Fact]
    public async Task Parsed_foods_take_their_numbers_from_the_matching_database_food()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());

        var items = await s.Matcher.ResolveAsync("",
        [
            Parsed("chicken breast", 200, FoodState.Raw),
            Parsed("white rice", 150, FoodState.Cooked),
            Parsed("banana", 120, FoodState.NotApplicable),
        ], default);

        items[0].MatchedName.Should().Contain("Chicken").And.Contain("breast").And.Contain("raw");
        items[0].Per100.Kcal.Should().BeInRange(100, 140);
        items[1].MatchedName.Should().Contain("Rice").And.Contain("cooked");
        items[1].Per100.Kcal.Should().BeInRange(110, 150);
        items[2].MatchedName.Should().StartWith("Bananas, raw");
        items.Should().OnlyContain(i => i.Confidence != Confidence.Low, "all three are in the USDA data");

        var potatoes = (await s.Matcher.ResolveAsync("", [Parsed("potatoes, boiled", 200, FoodState.Cooked)], default)).Single();
        potatoes.MatchedName.Should().Contain("Potatoes").And.NotContain(", skin,", "the skin on its own is not what \"potatoes\" means");
    }

    [Fact]
    public async Task Food_typed_in_Bulgarian_matches_the_Bulgarian_food_of_that_name()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        int soupId;
        await using (var db = fixture.CreateContext(null))
        {
            var soup = new FoodItem { Name = $"Bean soup {tag}", NameBg = $"Боб чорба {tag}", Source = FoodSource.Reference, KcalPer100g = 80, ProteinPer100g = 4.5m, CarbsPer100g = 10, FatPer100g = 2.5m };
            db.FoodItems.AddRange(soup, new FoodItem
            {
                Name = $"Пилешко месо от растителен произход {tag}", NameBg = $"Пилешко месо от растителен произход {tag}",
                Source = FoodSource.OpenFoodFacts, Barcode = "20" + Random.Shared.NextInt64(10_000_000_000, 99_999_999_999), KcalPer100g = 220, ProteinPer100g = 20,
            });
            await db.SaveChangesAsync();
            soupId = soup.Id;
        }

        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var items = await s.Matcher.ResolveAsync("",
        [
            new ParsedFoodItem("bean soup", $"боб чорба {tag}", 400, FoodState.NotApplicable, null, null, new Per100g(60, 4, 8, 1)),
            new ParsedFoodItem("chicken breast, skinless", $"пилешко месо {tag}", 150, FoodState.Cooked, null, null, new Per100g(165, 31, 0, 3.6m)),
        ], default);

        items[0].FoodItemId.Should().Be(soupId, "the Bulgarian dish beats USDA's condensed bean with bacon soup");
        items[1].MatchedName.Should().NotContain("растителен", "a plant-based substitute is not chicken");
    }

    [Fact]
    public async Task Unmatched_foods_fall_back_to_the_ai_estimate_with_low_confidence()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());

        var item = (await s.Matcher.ResolveAsync("", [new ParsedFoodItem("zzqx florp", "zzqx florp", 50, FoodState.NotApplicable, null, null, new Per100g(300, 10, 10, 1))], default)).Single();

        item.FoodItemId.Should().BeNull();
        item.Confidence.Should().Be(Confidence.Low);
        item.Per100.Kcal.Should().Be(300);
        item.Notes.Should().Contain(n => n.Format.Contains("don't add up"));
    }

    [Fact]
    public async Task Barcode_lookups_are_cached_as_shared_foods()
    {
        await using var a = new UserScope(fixture, await fixture.CreateUserAsync());
        const string code = "4006381333931";
        a.FoodDatabase.Products[code] = new ProductLookup(LookupStatus.Found,
            new ProductInfo(code, "Skyr", "Test dairy", new Per100g(63, 11, 4, 0.2m), 150));

        var first = await a.Foods.LookupBarcodeAsync(code, default);
        var again = await a.Foods.LookupBarcodeAsync(code, default);

        first.Item!.Id.Should().Be(again.Item!.Id);
        a.FoodDatabase.Lookups.Should().Be(1, "the second scan is served from the cache");

        await using var b = new UserScope(fixture, await fixture.CreateUserAsync());
        (await b.Foods.LookupBarcodeAsync(code, default)).Item!.Name.Should().Be("Skyr");
        b.FoodDatabase.Lookups.Should().Be(0, "the other user sees the shared cache too");

        await a.Invoking(x => x.Foods.LookupBarcodeAsync("123", default)).Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Manual_foods_are_private_unless_they_have_a_barcode()
    {
        await using var a = new UserScope(fixture, await fixture.CreateUserAsync());
        await using var b = new UserScope(fixture, await fixture.CreateUserAsync());
        var per100 = new Per100g(250, 10, 30, 10);

        var secret = await a.Foods.CreateManualAsync("Grandma's banitsa qwv", null, per100, null, share: false, default);
        var shared = await a.Foods.CreateManualAsync("Local yoghurt qwv", "96385074", per100, 400, share: true, default);

        await b.Invoking(x => x.Foods.GetAsync(secret.Id, default)).Should().ThrowAsync<NotFoundException>();
        (await b.Foods.GetAsync(shared.Id, default)).Barcode.Should().Be("96385074");
        (await b.Foods.SearchAsync("banitsa qwv", 10, 0.3, default)).Should().NotContain(f => f.Id == secret.Id);
        (await a.Foods.SearchAsync("banitsa qwv", 10, 0.3, default)).Should().Contain(f => f.Id == secret.Id && f.Own);
    }

    [Fact]
    public async Task Entries_store_totals_and_rescale_when_grams_change()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.FoodLog.AddAsync([new NewEntry(Day, Meal.Lunch, "Chicken", 200, new Per100g(120, 22.5m, 0, 2.6m), null, EntrySource.Manual)], default);
        var entry = (await s.FoodLog.DayAsync(Day, default)).Entries.Single();

        (entry.Kcal, entry.ProteinG, entry.FatG).Should().Be((240m, 45m, 5.2m));

        await s.FoodLog.UpdateAsync(entry.Id, "Chicken breast", 100, Meal.Dinner, default);
        s.Db.ChangeTracker.Clear();
        var updated = await s.FoodLog.GetAsync(entry.Id, default);
        (updated.Kcal, updated.ProteinG, updated.Meal).Should().Be((120m, 22.5m, Meal.Dinner));

        await s.Invoking(x => x.FoodLog.AddAsync([new NewEntry(Day, Meal.Lunch, "Huge", 5000, new Per100g(100, 1, 1, 1), null, EntrySource.Manual)], default))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Copying_a_meal_copies_only_that_meal_from_the_day_before()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var yesterday = Day.AddDays(-1);
        await s.FoodLog.AddAsync(
        [
            new NewEntry(yesterday, Meal.Breakfast, "Oats", 80, new Per100g(380, 13, 60, 7), null, EntrySource.Manual),
            new NewEntry(yesterday, Meal.Breakfast, "Milk", 250, new Per100g(64, 3.4m, 4.8m, 3.6m), null, EntrySource.Manual),
            new NewEntry(yesterday, Meal.Dinner, "Steak", 200, new Per100g(250, 26, 0, 15), null, EntrySource.Manual),
        ], default);

        (await s.FoodLog.CopyMealAsync(yesterday, Day, Meal.Breakfast, default)).Should().Be(2);

        var today = await s.FoodLog.DayAsync(Day, default);
        today.Entries.Select(e => e.Description).Should().BeEquivalentTo(["Oats", "Milk"]);
        today.Totals.Kcal.Should().Be(304 + 160);
    }

    [Fact]
    public async Task Recents_rank_by_frequency_and_remember_the_last_amount()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var per100 = new Per100g(100, 10, 10, 1);
        await s.FoodLog.AddAsync(
        [
            new NewEntry(Day.AddDays(-3), Meal.Snack, "Skyr", 150, per100, null, EntrySource.Manual),
            new NewEntry(Day.AddDays(-2), Meal.Snack, "Skyr", 170, per100, null, EntrySource.Manual),
            new NewEntry(Day.AddDays(-1), Meal.Snack, "Apple", 180, per100, null, EntrySource.Manual),
            new NewEntry(Day.AddDays(-40), Meal.Snack, "Old thing", 100, per100, null, EntrySource.Manual),
        ], default);

        var recents = await s.FoodLog.RecentsAsync(Day, default);

        recents.Select(r => r.Description).Should().Equal("Skyr", "Apple");
        recents[0].LastGrams.Should().Be(170);
        recents[0].Per100.Kcal.Should().Be(100);
    }

    [Fact]
    public async Task Users_cannot_see_or_change_each_others_food_entries()
    {
        await using var b = new UserScope(fixture, await fixture.CreateUserAsync());
        await b.FoodLog.AddAsync([new NewEntry(Day, Meal.Lunch, "B's lunch", 300, new Per100g(150, 10, 10, 5), null, EntrySource.Manual)], default);
        var entryId = (await b.FoodLog.DayAsync(Day, default)).Entries.Single().Id;

        await using var a = new UserScope(fixture, await fixture.CreateUserAsync());
        (await a.FoodLog.DayAsync(Day, default)).Entries.Should().BeEmpty();
        await a.Invoking(x => x.FoodLog.GetAsync(entryId, default)).Should().ThrowAsync<NotFoundException>();
        await a.Invoking(x => x.FoodLog.UpdateAsync(entryId, "mine", 1, Meal.Snack, default)).Should().ThrowAsync<NotFoundException>();
        await a.Invoking(x => x.FoodLog.DeleteAsync(entryId, default)).Should().ThrowAsync<NotFoundException>();
        (await a.FoodLog.CopyMealAsync(Day, Day.AddDays(1), Meal.Lunch, default)).Should().Be(0);
        (await a.FoodLog.RecentsAsync(Day, default)).Should().BeEmpty();

        b.Db.ChangeTracker.Clear();
        (await b.FoodLog.GetAsync(entryId, default)).Description.Should().Be("B's lunch");
    }
}

public class IntegrationParsingTests
{
    private static JsonNode Response(string json) =>
        new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = json } }) };

    [Fact]
    public void Parsed_items_are_read_and_impossible_ones_skipped()
    {
        var result = OpenRouterFoodParser.Read(Response("""
            {"items":[
              {"nameEn":"chicken breast","nameOriginal":"пилешко филе","grams":200,"state":"raw","preparation":"grilled","assumption":null,
               "kcalPer100g":120,"proteinPer100g":22.5,"carbsPer100g":0,"fatPer100g":2.6},
              {"nameEn":"rice","nameOriginal":"rice","grams":5000,"state":"cooked","kcalPer100g":130,"proteinPer100g":2.7,"carbsPer100g":28,"fatPer100g":0.3}
            ]}
            """), "google/gemini-3.5-flash-lite");

        result.Model.Should().Be("google/gemini-3.5-flash-lite");
        result.Items.Should().ContainSingle();
        result.Items[0].Should().BeEquivalentTo(new ParsedFoodItem("chicken breast", "пилешко филе", 200, FoodState.Raw, "grilled", null, new Per100g(120, 22.5m, 0, 2.6m)));
        result.Error!.ToString().Should().Contain("Skipped 1");
    }

    [Fact]
    public void Parser_garbage_becomes_a_friendly_error()
    {
        OpenRouterFoodParser.Read(Response("not json"), "m").Items.Should().BeEmpty();
        OpenRouterFoodParser.Read(new JsonObject(), "m").Error.Should().NotBeNull();
        OpenRouterFoodParser.Read(Response("""{"items":[]}"""), "m").Error!.ToString().Should().Be("No food found in that text.");
    }

    [Fact]
    public void Parser_keeps_sane_fiber_estimates_and_drops_impossible_ones_without_rejecting_the_food()
    {
        var result = OpenRouterFoodParser.Read(Response("""
            {"items":[
              {"nameEn":"oats","nameOriginal":"oats","grams":50,"state":"raw","kcalPer100g":380,"proteinPer100g":13,"carbsPer100g":60,"fatPer100g":7,"fiberPer100g":10,"saturatedFatPer100g":1.2},
              {"nameEn":"chicken","nameOriginal":"chicken","grams":200,"state":"raw","kcalPer100g":120,"proteinPer100g":22.5,"carbsPer100g":0,"fatPer100g":2.6,"fiberPer100g":null,"saturatedFatPer100g":9}
            ]}
            """), "m");

        result.Items.Select(i => (i.Estimated.Fiber, i.Estimated.SaturatedFat)).Should().Equal((10m, 1.2m), ((decimal?)null, (decimal?)null));
    }

    [Fact]
    public void Open_food_facts_converts_kilojoules_when_kcal_is_missing()
    {
        var product = JsonNode.Parse("""
            {"product_name":"Kiselo mlyako","brands":"Vereia, Other","serving_quantity":"400",
             "nutriments":{"energy-kj_100g":418.4,"proteins_100g":3.2,"carbohydrates_100g":4.1,"fat_100g":"3.6"}}
            """)!;

        var info = OpenFoodFactsClient.ReadProduct(product, "96385074")!;

        info.Per100.Should().Be(new Per100g(100, 3.2m, 4.1m, 3.6m));
        info.Brand.Should().Be("Vereia");
        info.ServingGrams.Should().Be(400);
        OpenFoodFactsClient.ReadProduct(JsonNode.Parse("""{"product_name":"x","nutriments":{}}""")!, "1").Should().BeNull();
    }

    [Fact]
    public void Open_food_facts_fiber_and_saturated_fat_are_read_when_present_and_sane()
    {
        static Per100g Read(string extra) => OpenFoodFactsClient.ReadProduct(JsonNode.Parse(
            $$$"""{"product_name":"Oats","nutriments":{"energy-kcal_100g":370,"proteins_100g":13,"carbohydrates_100g":60,"fat_100g":7{{{extra}}}}}""")!, "1")!.Per100;

        Read(""","fiber_100g":10,"saturated-fat_100g":1.2""").Should().Be(new Per100g(370, 13, 60, 7, 10, 1.2m));
        Read("").Should().Be(new Per100g(370, 13, 60, 7), "a product without them still counts as having nutrition data");
        Read(""","saturated-fat_100g":9,"fiber_100g":-1""").Should().Be(new Per100g(370, 13, 60, 7), "saturated fat above fat and negative fiber are data errors");
    }

    [Fact]
    public async Task Fake_parser_reads_amounts_and_splits_foods()
    {
        var result = await new FakeFoodParser().ParseAsync("200g chicken breast, 150 г cooked rice and a banana", default);

        // "a banana" is a count, not part of the name (Phase 11): one unit, 100 g until the matcher knows the serving.
        result.Items.Select(i => (i.NameEn, i.Grams, i.State, i.Units)).Should().Equal(
            ("chicken breast", 200m, FoodState.NotApplicable, (decimal?)null),
            ("cooked rice", 150m, FoodState.Cooked, (decimal?)null),
            ("banana", 100m, FoodState.NotApplicable, (decimal?)1m));
    }

    [Fact]
    public void Usda_csv_lines_with_quotes_parse()
    {
        UsdaSeed.ParseCsvLine("171077,\"Chicken, broiler, \"\"special\"\", raw\",120.0,22.5,0.0,2.62")
            .Should().Equal("171077", "Chicken, broiler, \"special\", raw", "120.0", "22.5", "0.0", "2.62");
    }
}
