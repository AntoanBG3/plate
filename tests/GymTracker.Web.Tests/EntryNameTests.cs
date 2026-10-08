using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Data.Seed;
using GymTracker.Web.Features.Food;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

/// <summary>Entry names: no amount in them, and an unnamed quick entry reads "Quick add" in either language (Phase 11).</summary>
[Collection(PostgresCollection.Name)]
public class EntryNameTests(PostgresFixture fixture)
{
    private static readonly DateOnly Day = new(2026, 9, 28);

    [Fact]
    public async Task A_saved_name_loses_its_amount_and_a_quick_one_may_be_empty()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.FoodLog.AddAsync([new NewEntry(Day, Meal.Lunch, "150 g cooked rice", 150, new Per100g(130, 2.7m, 28, 0.3m), null, EntrySource.Manual)], default);
        await s.FoodLog.AddQuickAsync(Day, Meal.Lunch, "", 500, 30, 50, 15, null, null, default);

        var names = (await s.FoodLog.DayAsync(Day, default)).Entries.Select(e => e.Description).ToList();
        names.Should().Equal("cooked rice", "");
    }

    [Fact]
    public async Task A_status_is_saved_beside_the_name_and_kept_when_a_meal_is_copied()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var rice = new Per100g(130, 2.7m, 28, 0.3m);
        await s.FoodLog.AddAsync([
            new NewEntry(Day, Meal.Lunch, "White rice,\nlong-grain", 150, rice, null, EntrySource.Ai, Status: " boiled "),
            new NewEntry(Day, Meal.Lunch, "Rice", 100, rice, null, EntrySource.Ai, Status: new string('x', 31)),
        ], default);
        await s.FoodLog.CopyMealAsync(Day, Day.AddDays(1), Meal.Lunch, default);

        var day = (await s.FoodLog.DayAsync(Day, default)).Entries;
        day.Select(e => (e.Description, e.Status)).Should().Equal(("White rice, long-grain", "boiled"), ("Rice", (string?)null));
        (await s.FoodLog.DayAsync(Day.AddDays(1), default)).Entries.Select(e => e.Status).Should().Equal("boiled", null);
    }

    [Fact]
    public async Task The_fallback_parser_keeps_the_amount_out_of_the_name()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var (_, items, _) = await s.ParseRuns.ParseAsync("150 g cooked rice, 200g chicken breast", default);
        items.Select(i => (i.Typed, i.Grams)).Should().Equal(("cooked rice", 150m), ("chicken breast", 200m));
        items.Should().OnlyContain(i => !char.IsDigit(i.Description[0]), "a matched row takes the food's name, which has no amount either");
    }

    [Fact]
    public async Task Old_default_quick_names_are_cleared_once_at_startup()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.FoodLog.AddQuickAsync(Day, Meal.Lunch, "Quick add", 500, 30, 50, 15, null, null, default);
        await s.FoodLog.AddQuickAsync(Day, Meal.Lunch, "Бързо добавяне", 400, 30, 50, 15, null, null, default);
        await s.FoodLog.AddQuickAsync(Day, Meal.Lunch, "Burger at Joe's", 900, 40, 60, 50, null, null, default);

        await using var system = fixture.CreateContext(null);
        await DataFixups.RunAsync(system, default);
        await DataFixups.RunAsync(system, default);

        (await s.Db.FoodLog.AsNoTracking().Where(e => e.Date == Day).OrderBy(e => e.Id).Select(e => e.Description).ToListAsync())
            .Should().Equal("", "", "Burger at Joe's");
    }

    [Fact]
    public async Task Unnamed_quick_entries_read_Quick_add_in_the_users_language()
    {
        await using var app = new AppFactory(fixture);
        var (client, email) = await app.SignedInClientAsync();
        await using var system = fixture.CreateContext(null);
        var userId = await system.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        await using var s = new UserScope(fixture, userId);
        var today = GymTracker.Core.Time.UserClock.Today(DateTimeOffset.UtcNow, UserProfile.DefaultTimeZoneId);
        await s.FoodLog.AddQuickAsync(today, Meal.Lunch, "", 500, 30, 50, 15, null, null, default);

        System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/food")).Should().Contain(">Quick add</span>");
        await s.Db.Profiles.ExecuteUpdateAsync(p => p.SetProperty(x => x.Language, Language.Bulgarian));
        System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/food")).Should().Contain(">Бързо добавяне</span>");
        System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/today")).Should().Contain("Бързо добавяне").And.NotContain("Quick add");
    }

    [Fact]
    public async Task The_quick_add_sheet_names_its_meal_and_marks_every_macro_optional()
    {
        await using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();
        var html = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/food/quick?date=2026-09-28&meal=Lunch"));
        html.Should().Contain("Quick add to lunch");
        System.Text.RegularExpressions.Regex.Matches(html, "placeholder=\"Optional\"").Count.Should().Be(5);
    }

    [Fact]
    public async Task The_entry_page_names_the_database_food_and_gives_units()
    {
        await using var app = new AppFactory(fixture);
        var (client, email) = await app.SignedInClientAsync();
        await using var system = fixture.CreateContext(null);
        var userId = await system.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        await using var s = new UserScope(fixture, userId);
        var rice = await s.Db.FoodItems.FirstAsync(f => f.Name.StartsWith("Rice, white"));
        await s.FoodLog.AddAsync([new NewEntry(Day, Meal.Lunch, "rice", 150, rice.Per100(), rice.Id, EntrySource.Search)], default);
        var id = (await s.FoodLog.DayAsync(Day, default)).Entries.Single().Id;

        var html = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync($"/food/entries/{id}"));
        html.Should().Contain(rice.Name).And.Contain("<small> g</small></div><div class=\"stat-label\">protein");
    }
}
