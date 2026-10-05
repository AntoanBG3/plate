using FluentAssertions;
using Microsoft.Playwright;

namespace GymTracker.E2E.Tests;

/// <summary>The active workout screen (SPEC 7 Train, Phase 11).</summary>
[Collection(E2ECollection.Name)]
public class SessionTests(E2EFixture app)
{
    private static ILocator Rows(IPage page, int card = 0) => page.Locator(".exercise").Nth(card).Locator(".rows.working .set-row");

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Editing_a_logged_set_saves_without_tapping_the_check_again(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press");
        await phone.StartSessionAsync(template);

        await phone.LogSetAsync(Rows(phone.Page).First, "60", "10");
        await Rows(phone.Page).First.And(phone.Page.Locator(".set-row.done")).WaitForAsync();

        await Rows(phone.Page).First.Locator("input[name=weightKg]").FillAsync("62.5");
        await phone.Page.Locator(".large-title").TapAsync(); // moves focus away, as tapping elsewhere does on a phone
        await phone.Page.Locator(".set-row.done.is-saved").WaitForAsync(new() { Timeout = 5_000 });

        await phone.GoAsync(phone.Page.Url);
        (await Rows(phone.Page).First.Locator("input[name=weightKg]").InputValueAsync()).Should().Be("62.5");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task An_edit_still_being_typed_is_saved_when_leaving_the_page(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press");
        var session = await phone.StartSessionAsync(template);

        await phone.LogSetAsync(Rows(phone.Page).First, "60", "10");
        await phone.Page.Locator(".set-row.done").WaitForAsync();
        await Rows(phone.Page).First.Locator("input[name=reps]").FillAsync("11");

        // Straight to another tab, with the field still focused.
        await phone.NavigateByAsync(() => phone.TapAsync(".tabbar a[href='/today']"));
        await phone.GoAsync($"/train/sessions/{session}");
        (await Rows(phone.Page).First.Locator("input[name=reps]").InputValueAsync()).Should().Be("11");
        phone.ShouldHaveNoErrors();
    }
}
