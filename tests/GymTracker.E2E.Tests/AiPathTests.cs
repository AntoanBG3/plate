using FluentAssertions;
using GymTracker.Core.Analytics;
using GymTracker.Core.Domain;
using GymTracker.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.E2E.Tests;

/// <summary>The pages that need an OpenRouter key, against stubbed models (AiStubs.cs); QA couldn't test them (Phase 11).</summary>
[Collection(E2ECollection.Name)]
public class AiPathTests(E2EFixture app)
{
    private async Task<string> UserIdAsync(string email)
    {
        await using var db = app.Db.CreateContext(null);
        return await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task The_setup_chat_fills_the_card_and_saves_the_profile(string browser)
    {
        var (email, password) = await app.App.CreateUserAsync(onboarded: false);
        await using var phone = await app.PhoneAsync(browser, ai: true);
        await phone.SignInAsync(email, password);
        await phone.GoAsync("/welcome");

        await phone.NavigateByAsync(() => phone.Page.Locator("button", new() { HasText = "Let's set you up" }).TapAsync());
        await phone.Page.FillAsync("#setup-text", "I'm a 30-year-old man, 180 cm, 80 kg, desk job, want to keep my weight.");
        await phone.NavigateByAsync(() => phone.Page.Locator("[data-composer] button[type=submit]").TapAsync());
        (await phone.Page.Locator("main").InnerTextAsync()).Should().Contain("Thanks, that's everything I need.");
        await phone.Page.Locator(".reply-chip", new() { HasText = "Looks right" }).WaitForAsync();

        // The card under the chat holds what the model learned; Save goes through the same checks as Settings.
        (await phone.Page.InputValueAsync("[data-setup-form] [name=heightCm]")).Should().Be("180");
        await phone.NavigateByAsync(() => phone.Page.Locator("[data-setup-form] button[type=submit]").TapAsync());
        new Uri(phone.Page.Url).AbsolutePath.Should().Be("/today");

        await using var db = app.Db.CreateContext(null);
        var id = await UserIdAsync(email);
        var profile = await db.Profiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.UserId == id);
        profile.OnboardedAt.Should().NotBeNull();
        profile.HeightCm.Should().Be(180);
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Today_loads_last_weeks_summary_after_the_page(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        var lastMonday = IsoWeek.Of(UserClock.Today(DateTimeOffset.UtcNow, UserProfile.DefaultTimeZoneId)).Previous().Monday;
        await using (var db = app.Db.CreateContext(null))
        {
            var id = await UserIdAsync(phone.Email);
            for (var i = 0; i < 3; i++)
            {
                db.Bodyweights.Add(new BodyweightEntry { UserId = id, Date = lastMonday.AddDays(i * 2), WeightKg = 80m - i * 0.2m });
            }

            await db.SaveChangesAsync();
        }

        await phone.GoAsync("/today");
        var card = phone.Page.Locator(".weekly-summary:not(.loading)");
        await card.WaitForAsync();
        (await card.InnerTextAsync()).Should().Contain("You weighed in 3 times last week.").And.Contain("Weigh in every morning.");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Typed_food_is_parsed_by_the_model_and_an_unknown_food_is_its_estimate(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        var today = UserClock.Today(DateTimeOffset.UtcNow, UserProfile.DefaultTimeZoneId).ToString("yyyy-MM-dd");
        await phone.GoAsync($"/food/add?date={today}&meal=Lunch");
        await phone.Page.FillAsync("#text", "a banana and a bowl of zorblax qwimbly");
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/parse'] button[type=submit]").TapAsync());

        var rows = phone.Page.Locator(".result");
        (await rows.CountAsync()).Should().Be(2);
        // The matched banana gets the namer's short name and status; the full database name is under it.
        (await rows.Nth(0).Locator("textarea[name$='Description']").InputValueAsync()).Should().Be("Banana");
        (await rows.Nth(0).Locator("[data-status]").TextContentAsync()).Should().Be("raw");
        (await rows.Nth(0).Locator("[data-using]").TextContentAsync()).Should().StartWith("Database: Bananas");
        // Nothing in the database is like it, so the row keeps the model's own numbers.
        (await rows.Nth(1).InnerTextAsync()).Should().Contain("AI estimate").And.NotContain("Database:");

        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/confirm'] button[type=submit]").Last.TapAsync());
        await using var db = app.Db.CreateContext(await UserIdAsync(phone.Email));
        (await db.FoodLog.Select(e => e.AiModel).ToListAsync()).Should().Equal("test/parser", "test/parser");
        phone.ShouldHaveNoErrors();
    }
}
