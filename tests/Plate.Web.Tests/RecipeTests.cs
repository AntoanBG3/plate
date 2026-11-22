using FluentAssertions;
using Plate.Core.Domain;
using Plate.Core.Nutrition;
using Plate.Web.Data;
using Plate.Web.Features;
using Plate.Web.Features.Food;
using Plate.Web.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Tests;

/// <summary>Recipes: built from ingredients, logged like any food, private to their account (SPEC 5.12).</summary>
public class RecipeTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static RecipeService Recipes(UserScope s) =>
        new(s.Db, new FixedCurrentUser(s.UserId), TimeProvider.System, new FakeFoodParser(), s.Matcher, s.Foods);

    private static async Task<FoodMatch> FoodAsync(UserScope s, string query) => (await s.Foods.SearchAsync(query, 1, 0.3, default)).First();

    private static async Task<FoodItem> MusakaAsync(UserScope s, string name = "Мусака", decimal? cooked = null)
    {
        var potato = await FoodAsync(s, "potatoes raw");
        var mince = await FoodAsync(s, "ground pork");
        return await Recipes(s).SaveAsync(null, name, cooked, [
            new("Картофи", 800, potato.Id, new Per100g(1, 1, 1, 1)), // posted values are ignored for a database food
            new("Кайма", 500, mince.Id, mince.Per100),
            new("Олио", 30, null, new Per100g(884, 0, 0, 100, 0, 15)),
        ], default);
    }

    [Fact]
    public async Task A_recipe_is_a_private_food_with_values_from_its_ingredients()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var potato = await FoodAsync(s, "potatoes raw");
        var mince = await FoodAsync(s, "ground pork");

        var food = await MusakaAsync(s, cooked: 1100);

        food.Source.Should().Be(FoodSource.Recipe);
        food.OwnerUserId.Should().Be(s.UserId);
        var expected = RecipeMath.Calculate([new(800, potato.Per100), new(500, mince.Per100), new(30, new Per100g(884, 0, 0, 100, 0, 15))], 1100).Per100;
        food.Per100().Should().Be(expected, "the database foods' values are used, not the posted ones");
        (await s.Foods.SearchAsync("мусака", 5, 0.3, default)).Should().Contain(f => f.Id == food.Id && f.Own);
        var draft = await Recipes(s).GetAsync(food.Id, default);
        draft.Items.Select(i => i.Description).Should().Equal("Картофи", "Кайма", "Олио");
        draft.YieldGrams.Should().Be(1100);
    }

    [Fact]
    public async Task Editing_a_recipe_changes_later_entries_only_and_Recent_offers_the_new_values()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var food = await MusakaAsync(s);
        var kcalBefore = food.KcalPer100g;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await s.FoodLog.AddAsync([new(today.AddDays(-1), Meal.Dinner, food.Name, 350, food.Per100(), food.Id, EntrySource.Search)], default);
        var before = await s.Db.FoodLog.SingleAsync();

        var draft = await Recipes(s).GetAsync(food.Id, default);
        await Recipes(s).SaveAsync(food.Id, "Мусака", 900, draft.Items.Select(i => new RecipeIngredientInput(i.Description, i.Grams, i.FoodItemId, i.Per100)).ToList(), default);

        var entry = await s.Db.FoodLog.AsNoTracking().SingleAsync();
        entry.Kcal.Should().Be(before.Kcal, "yesterday keeps what it was logged with");
        var updated = await s.Db.FoodItems.AsNoTracking().SingleAsync(f => f.Id == food.Id);
        updated.KcalPer100g.Should().BeGreaterThan(kcalBefore, "the same dish weighed less when cooked");
        (await s.FoodLog.RecentsAsync(today, Meal.Dinner, default)).Single().Per100.Should().Be(updated.Per100());
    }

    [Fact]
    public async Task Another_account_cannot_open_edit_delete_search_or_use_a_recipe()
    {
        await using var mine = new UserScope(fixture, await fixture.CreateUserAsync());
        await using var theirs = new UserScope(fixture, await fixture.CreateUserAsync());
        var food = await MusakaAsync(mine, "Тайна мусака");

        await Recipes(theirs).Invoking(r => r.GetAsync(food.Id, default)).Should().ThrowAsync<NotFoundException>();
        await Recipes(theirs).Invoking(r => r.SaveAsync(food.Id, "Mine now", null, [new("x", 100, null, new Per100g(100, 1, 1, 1))], default)).Should().ThrowAsync<NotFoundException>();
        await Recipes(theirs).Invoking(r => r.DeleteAsync(food.Id, default)).Should().ThrowAsync<NotFoundException>();
        await Recipes(theirs).Invoking(r => r.SaveAsync(null, "Copy", null, [new("x", 100, food.Id, food.Per100())], default)).Should().ThrowAsync<NotFoundException>();
        (await theirs.Foods.SearchAsync("Тайна мусака", 5, 0.3, default)).Should().NotContain(f => f.Id == food.Id);
        (await Recipes(theirs).ListAsync(default)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_recipe_cannot_be_an_ingredient()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var food = await MusakaAsync(s);

        await Recipes(s).Invoking(r => r.SaveAsync(null, "Double", null, [new("Мусака", 300, food.Id, food.Per100())], default)).Should().ThrowAsync<ValidationException>();
        await Recipes(s).Invoking(r => r.IngredientAsync(food.Id, 300, default)).Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task A_second_recipe_with_the_same_name_is_refused_but_another_account_may_use_it()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await using var other = new UserScope(fixture, await fixture.CreateUserAsync());
        await MusakaAsync(s, "Баница");

        await s.Invoking(x => MusakaAsync(x, "баница ")).Should().ThrowAsync<ValidationException>();
        (await MusakaAsync(other, "Баница")).Name.Should().Be("Баница");
    }

    [Fact]
    public async Task A_cooked_weight_is_checked_again_when_the_ingredients_change()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var food = await MusakaAsync(s, cooked: 1100);

        await Recipes(s).Invoking(r => r.SaveAsync(food.Id, "Мусака", 1100, [new("Олио", 30, null, new Per100g(884, 0, 0, 100))], default))
            .Should().ThrowAsync<ValidationException>("1100 g is far over 300% of 30 g");
    }

    [Fact]
    public async Task Deleting_a_recipe_keeps_the_entries_logged_from_it()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var food = await MusakaAsync(s);
        await s.FoodLog.AddAsync([new(DateOnly.FromDateTime(DateTime.UtcNow), Meal.Dinner, food.Name, 350, food.Per100(), food.Id, EntrySource.Search)], default);

        await Recipes(s).DeleteAsync(food.Id, default);

        var entry = await s.Db.FoodLog.AsNoTracking().SingleAsync();
        (entry.Description, entry.FoodItemId).Should().Be((food.Name, null));
        (await s.Db.RecipeIngredients.IgnoreQueryFilters().AnyAsync(i => i.RecipeFoodItemId == food.Id)).Should().BeFalse();
    }

    /// <summary>Reads "ориз" as cooked rice, as the live parser does for rice and pasta without a state.</summary>
    private sealed class CookedRiceParser : IFoodParser
    {
        public Task<FoodParseResult> ParseAsync(string text, CancellationToken ct) => Task.FromResult(new FoodParseResult(
            [new ParsedFoodItem("rice", "ориз", 200, FoodState.Cooked, null, null, new Per100g(130, 2.7m, 28, 0.3m), GramsStated: true)], null, "test"));
    }

    [Fact]
    public async Task A_weighed_staple_starts_on_the_dry_food_in_a_recipe()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var recipes = new RecipeService(s.Db, new FixedCurrentUser(s.UserId), TimeProvider.System, new CookedRiceParser(), s.Matcher, s.Foods);

        var (items, _) = await recipes.ParseIngredientsAsync("200 г ориз", default);

        var rice = items.Single();
        rice.State.Should().Be(FoodState.Raw, "a recipe weighs what goes into the pot");
        rice.Per100.Kcal.Should().BeGreaterThan(300, "dry rice, not cooked");
        rice.Choice!.Current.Should().Be(FoodState.Raw);
        rice.Choice.Other.State.Should().Be(FoodState.Cooked, "the cooked food is one tap away");
        rice.Notes.Select(n => n.ToString()).Should().Contain("Counted as weighed before cooking.");
        (await s.Db.ParseRuns.AnyAsync()).Should().BeFalse("ingredients aren't recorded as a meal's parse");
    }

    [Fact]
    public async Task A_recipe_named_after_a_staple_gets_no_cooked_or_dry_question()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var rice = await FoodAsync(s, "rice white long-grain raw");
        var food = await Recipes(s).SaveAsync(null, "Rice with chicken", null,
            // About 170 kcal per 100 g, which reads as cooked rice by its energy: without the rule it would be asked about.
            [new("rice", 100, rice.Id, rice.Per100), new("chicken", 400, null, new Per100g(120, 22, 0, 3))], default);

        var outcome = await s.ParseRuns.ParseAsync("300 g rice with chicken", default);

        var row = outcome.Items.Single();
        row.FoodItemId.Should().Be(food.Id, "the account's own dish matches");
        row.Choice.Should().BeNull();
    }
}
