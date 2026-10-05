using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Core.Time;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Food;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

/// <summary>Future days can't be logged; past days show the targets in force then (SPEC 7 Food, Phase 11).</summary>
[Collection(PostgresCollection.Name)]
public class FoodDaysTests(PostgresFixture fixture)
{
    private static DateOnly Today => UserClock.Today(DateTimeOffset.UtcNow, UserProfile.DefaultTimeZoneId);

    [Fact]
    public async Task Nothing_can_be_logged_on_a_future_day()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var tomorrow = Today.AddDays(1);
        var oats = new Per100g(380, 13, 60, 7);

        await s.Invoking(x => x.FoodLog.AddAsync([new NewEntry(tomorrow, Meal.Breakfast, "Oats", 80, oats, null, EntrySource.Manual)], default))
            .Should().ThrowAsync<ValidationException>();
        await s.Invoking(x => x.FoodLog.AddQuickAsync(tomorrow, Meal.Lunch, "Wrap", 500, 30, 50, 15, null, null, default))
            .Should().ThrowAsync<ValidationException>();
        await s.Invoking(x => x.FoodLog.CopyMealAsync(Today, tomorrow, Meal.Lunch, default))
            .Should().ThrowAsync<ValidationException>();

        await s.FoodLog.AddQuickAsync(Today, Meal.Lunch, "Wrap", 500, 30, 50, 15, null, null, default);
        var meal = await s.SavedMeals.SaveFromMealAsync(Today, Meal.Lunch, "Usual lunch", default);
        await s.Invoking(x => x.SavedMeals.LogAsync(meal.Id, tomorrow, Meal.Lunch, [null], default))
            .Should().ThrowAsync<ValidationException>();
        (await s.Db.FoodLog.CountAsync()).Should().Be(1, "only today's entry was saved");
    }

    [Fact]
    public async Task The_food_pages_send_a_future_day_back_to_today()
    {
        await using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();
        var tomorrow = Today.AddDays(1).ToString("yyyy-MM-dd");

        AppFactory.IsRedirectTo(await client.GetAsync($"/food?date={tomorrow}"), "/food").Should().BeTrue();
        AppFactory.IsRedirectTo(await client.GetAsync($"/food/add?date={tomorrow}&meal=Lunch"), "/food").Should().BeTrue();
        var page = await client.GetStringAsync("/food");
        page.Should().NotContain("aria-label=\"Next day\"", "today has no next day to go to");
    }

    [Fact]
    public async Task A_past_day_uses_the_estimate_in_force_that_day()
    {
        await using var app = new AppFactory(fixture);
        var (client, email) = await app.SignedInClientAsync();
        await using var system = fixture.CreateContext(null);
        var userId = await system.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        await using var s = new UserScope(fixture, userId);
        await s.Db.Profiles.Where(p => p.UserId == userId).ExecuteUpdateAsync(p => p
            .SetProperty(x => x.Sex, Sex.Male).SetProperty(x => x.BirthYear, 1990).SetProperty(x => x.HeightCm, 180).SetProperty(x => x.Goal, Goal.Maintain));
        await s.Bodyweights.SetAsync(Today.AddDays(-10), 80, default);
        s.Db.MaintenanceEstimates.Add(new MaintenanceEstimate
        {
            UserId = userId, CalculatedOn = Today.AddDays(-5), EstimatedTdeeKcal = 2600, TargetKcal = 2600, TargetProteinG = 144,
            DaysOfData = 0, LoggingCompleteness = 0, Method = MaintenanceMethod.Initial,
        });
        await s.Db.SaveChangesAsync();

        var withTarget = await client.GetStringAsync($"/food?date={Today.AddDays(-2):yyyy-MM-dd}");
        withTarget.Should().Contain("of 2600");

        var before = await client.GetStringAsync($"/food?date={Today.AddDays(-8):yyyy-MM-dd}");
        before.Should().Contain("Targets start on").And.NotContain("of 2600");
    }

    [Fact]
    public async Task Copy_yesterday_is_offered_only_for_meals_yesterday_has()
    {
        await using var app = new AppFactory(fixture);
        var (client, email) = await app.SignedInClientAsync();
        System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/food")).Should().NotContain("Copy yesterday's");

        await using var system = fixture.CreateContext(null);
        var userId = await system.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        await using var s = new UserScope(fixture, userId);
        await s.FoodLog.AddQuickAsync(Today.AddDays(-1), Meal.Lunch, "Wrap", 500, 30, 50, 15, null, null, default);

        var page = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/food"));
        page.Should().Contain("Copy yesterday's lunch").And.NotContain("Copy yesterday's breakfast").And.NotContain("Copy yesterday's dinner");
    }

    [Fact]
    public async Task Nothing_to_copy_is_a_neutral_note_not_a_success()
    {
        await using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/food");
        var request = new HttpRequestMessage(HttpMethod.Post, "/food/copy")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["date"] = Today.ToString("yyyy-MM-dd"), ["meal"] = "Dinner", ["__RequestVerificationToken"] = token }),
        };
        var response = await client.SendAsync(request);
        var page = await client.GetStringAsync(response.Headers.Location!.OriginalString);
        page.Should().Contain("banner is-info").And.NotContain("banner is-success");
    }
}
