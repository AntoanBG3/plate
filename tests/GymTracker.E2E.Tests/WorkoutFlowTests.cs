using FluentAssertions;

namespace GymTracker.E2E.Tests;

/// <summary>Finishing a workout and the next one: summary, Today's Done today, and the Last time line (SPEC 7).</summary>
public class WorkoutFlowTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Summary_today_and_the_next_session_show_what_was_done(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press", "Barbell row");
        var session = await phone.StartSessionAsync(template);
        var rows = phone.Page.Locator(".exercise").First.Locator(".rows.working .set-row");
        await phone.LogSetAsync(rows.Nth(0), "60", "10");
        await phone.Page.Locator(".rows.working .set-row.done").Nth(0).WaitForAsync();
        await phone.LogSetAsync(rows.Nth(1), "60", "9");
        await phone.Page.Locator(".rows.working .set-row.done").Nth(1).WaitForAsync();

        await phone.GoAsync(new Uri(await phone.PostAsync($"/train/sessions/{session}/finish")).PathAndQuery);

        // Summary: sets kept apart, back to Train, and the first-ever bench press listed as a baseline, not a record.
        (await phone.Page.Locator("[data-exercises] .row-sub").First.TextContentAsync()).Should().Be("60 × 10 · 60 × 9");
        (await phone.Page.Locator(".topbar-back").GetAttributeAsync("href")).Should().Be("/train");
        (await phone.Page.Locator(".topbar-back").TextContentAsync())!.Trim().Should().Be("Train");
        (await phone.Page.Locator("[data-first-time]").TextContentAsync()).Should().Contain("Bench press");
        (await phone.Page.Locator(".stat").Nth(2).Locator(".stat-value").TextContentAsync()).Should().Be("0");

        await phone.NavigateByAsync(() => phone.TapAsync(".tabbar a[href='/today']"));
        var done = await phone.Page.Locator("[data-done-today]").TextContentAsync();
        done.Should().Contain("Upper").And.Contain("2 sets");
        (await phone.Page.Locator("[data-up-next]").CountAsync()).Should().Be(1, "the next workout still shows, under it");

        await phone.StartSessionAsync(template);
        (await phone.Page.Locator(".exercise").First.Locator("[data-last-time]").TextContentAsync())
            .Should().Be("Last time: 60 × 10 · 60 × 9");
        (await phone.Page.Locator(".exercise").Nth(1).Locator("[data-last-time]").CountAsync()).Should().Be(0, "no row was ever logged");
        phone.ShouldHaveNoErrors();
    }
}
