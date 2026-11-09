using FluentAssertions;
using Plate.Core.Domain;
using Plate.Core.Nutrition;
using Plate.Web.Features;
using Plate.Web.Features.Food;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Tests;

public class SavedMealTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly DateOnly Day = new(2026, 9, 28);

    private static async Task<UserScope> WithBreakfastAsync(PostgresFixture fixture)
    {
        var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.FoodLog.AddAsync(
        [
            new NewEntry(Day, Meal.Breakfast, "Oats", 80, new Per100g(380, 13, 60, 7), null, EntrySource.Manual),
            new NewEntry(Day, Meal.Breakfast, "Milk", 250, new Per100g(64, 3.4m, 4.8m, 3.6m), null, EntrySource.Manual),
            new NewEntry(Day, Meal.Lunch, "Rice", 200, new Per100g(130, 2.7m, 28, 0.3m), null, EntrySource.Manual),
        ], default);
        await s.FoodLog.AddQuickAsync(Day, Meal.Breakfast, "Coffee out", 120, 4, 12, 6, null, null, default);
        return s;
    }

    [Fact]
    public async Task Saving_copies_only_that_meal_of_that_day()
    {
        await using var s = await WithBreakfastAsync(fixture);

        var saved = await s.SavedMeals.SaveFromMealAsync(Day, Meal.Breakfast, "Usual breakfast", default);

        s.Db.ChangeTracker.Clear();
        var loaded = await s.SavedMeals.GetAsync(saved.Id, default);
        loaded.Items.Select(i => (i.Description, i.Grams)).Should().Equal(("Oats", 80m), ("Milk", 250m), ("Coffee out", 0m));
    }

    [Fact]
    public async Task Logging_scales_each_item_to_its_new_grams()
    {
        await using var s = await WithBreakfastAsync(fixture);
        var saved = await s.SavedMeals.SaveFromMealAsync(Day, Meal.Breakfast, "Usual breakfast", default);
        var next = Day.AddDays(1);

        var count = await s.SavedMeals.LogAsync(saved.Id, next, Meal.Snack, [100m, null, 999m], default);

        count.Should().Be(3);
        var entries = (await s.FoodLog.DayAsync(next, default)).Entries;
        entries.Should().OnlyContain(e => e.Meal == Meal.Snack);
        entries.Single(e => e.Description == "Oats").Kcal.Should().Be(380);
        entries.Single(e => e.Description == "Milk").Grams.Should().Be(250);
        var coffee = entries.Single(e => e.Description == "Coffee out");
        (coffee.Source, coffee.Grams, coffee.Kcal).Should().Be((EntrySource.Quick, 0m, 120m));
    }

    [Fact]
    public async Task An_empty_meal_cannot_be_saved()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.Invoking(x => x.SavedMeals.SaveFromMealAsync(Day, Meal.Dinner, "Nothing", default)).Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Logging_drops_a_food_link_that_is_no_longer_visible()
    {
        await using var s = await WithBreakfastAsync(fixture);
        var saved = await s.SavedMeals.SaveFromMealAsync(Day, Meal.Breakfast, "Usual breakfast", default);
        // Point an item at a food this user can't see (another user's private item).
        await using var other = new UserScope(fixture, await fixture.CreateUserAsync());
        var hidden = new FoodItem { Name = "Private oats", OwnerUserId = other.UserId, KcalPer100g = 380, Source = FoodSource.Manual };
        other.Db.FoodItems.Add(hidden);
        await other.Db.SaveChangesAsync();
        await s.Db.SavedMealItems.Where(i => i.SavedMealId == saved.Id && i.Description == "Oats")
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.FoodItemId, hidden.Id));

        await s.SavedMeals.LogAsync(saved.Id, Day.AddDays(2), Meal.Breakfast, [null, null, null], default);

        (await s.FoodLog.DayAsync(Day.AddDays(2), default)).Entries.Single(e => e.Description == "Oats").FoodItemId.Should().BeNull();
    }

    [Fact]
    public async Task Another_users_saved_meal_is_not_found()
    {
        await using var s = await WithBreakfastAsync(fixture);
        var saved = await s.SavedMeals.SaveFromMealAsync(Day, Meal.Breakfast, "Mine", default);
        await using var other = new UserScope(fixture, await fixture.CreateUserAsync());

        await other.Invoking(x => x.SavedMeals.LogAsync(saved.Id, Day, Meal.Lunch, [], default)).Should().ThrowAsync<NotFoundException>();
        await other.Invoking(x => x.SavedMeals.DeleteAsync(saved.Id, default)).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Names_are_checked_and_renamed()
    {
        await using var s = await WithBreakfastAsync(fixture);
        await s.Invoking(x => x.SavedMeals.SaveFromMealAsync(Day, Meal.Breakfast, "  ", default)).Should().ThrowAsync<ValidationException>();
        var saved = await s.SavedMeals.SaveFromMealAsync(Day, Meal.Breakfast, "A", default);

        await s.SavedMeals.RenameAsync(saved.Id, " Breakfast B ", default);
        await s.Invoking(x => x.SavedMeals.RenameAsync(saved.Id, new string('x', 61), default)).Should().ThrowAsync<ValidationException>();

        s.Db.ChangeTracker.Clear();
        (await s.SavedMeals.GetAsync(saved.Id, default)).Name.Should().Be("Breakfast B");
    }

    [Fact]
    public async Task The_posted_form_keeps_grams_lined_up_around_a_quick_item()
    {
        await using var app = new AppFactory(fixture);
        var (client, email) = await app.SignedInClientAsync();
        await using var system = fixture.CreateContext(null);
        var userId = await system.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        await using var s = new UserScope(fixture, userId);
        await s.FoodLog.AddAsync([new NewEntry(Day, Meal.Breakfast, "Oats", 80, new Per100g(380, 13, 60, 7), null, EntrySource.Manual)], default);
        await s.FoodLog.AddQuickAsync(Day, Meal.Breakfast, "Coffee out", 120, 4, 12, 6, null, null, default);
        await s.FoodLog.AddAsync([new NewEntry(Day, Meal.Breakfast, "Milk", 250, new Per100g(64, 3.4m, 4.8m, 3.6m), null, EntrySource.Manual)], default);
        var saved = await s.SavedMeals.SaveFromMealAsync(Day, Meal.Breakfast, "Usual", default);
        var token = await AppFactory.TokenAsync(client, "/food");

        // The sheet sends one grams field per item, empty for the quick one.
        var response = await client.PostAsync($"/food/saved-meals/{saved.Id}/log", new FormUrlEncodedContent(
        [
            new("date", "2026-09-30"), new("meal", "Snack"),
            new("grams", "100"), new("grams", ""), new("grams", "300"),
            new("__RequestVerificationToken", token), new("requestId", Guid.NewGuid().ToString()),
        ]));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Redirect);
        var entries = (await s.FoodLog.DayAsync(new DateOnly(2026, 9, 30), default)).Entries;
        entries.Select(e => (e.Description, e.Grams)).Should().BeEquivalentTo([("Oats", 100m), ("Coffee out", 0m), ("Milk", 300m)]);
    }

    [Fact]
    public async Task An_error_about_one_item_stays_in_Bulgarian_after_the_redirect()
    {
        await using var app = new AppFactory(fixture);
        var (client, email) = await app.SignedInClientAsync();
        await using var system = fixture.CreateContext(null);
        var userId = await system.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        await using var s = new UserScope(fixture, userId);
        await s.FoodLog.AddAsync([new NewEntry(Day, Meal.Breakfast, "Oats", 80, new Per100g(380, 13, 60, 7), null, EntrySource.Manual)], default);
        var saved = await s.SavedMeals.SaveFromMealAsync(Day, Meal.Breakfast, "Usual", default);
        await s.Db.Profiles.ExecuteUpdateAsync(u => u.SetProperty(p => p.Language, Language.Bulgarian));
        var token = await AppFactory.TokenAsync(client, "/food");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/food/saved-meals/{saved.Id}/log")
        {
            Content = new FormUrlEncodedContent([new("date", "2026-10-01"), new("meal", "Snack"), new("grams", "0"), new("__RequestVerificationToken", token), new("requestId", Guid.NewGuid().ToString())]),
        };
        request.Headers.Referrer = new Uri("https://localhost/food");
        var response = await client.SendAsync(request);
        var page = await client.GetStringAsync(response.Headers.Location!.ToString());

        System.Net.WebUtility.HtmlDecode(page).Should().Contain("Oats: Грамовете трябва да са над 0");
    }
}
