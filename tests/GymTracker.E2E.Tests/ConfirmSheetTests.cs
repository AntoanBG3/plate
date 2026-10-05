using FluentAssertions;

namespace GymTracker.E2E.Tests;

/// <summary>Destructive and final actions ask in the app's own sheet, never the browser's confirm (SPEC 7).</summary>
[Collection(E2ECollection.Name)]
public class ConfirmSheetTests(E2EFixture app)
{
    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Finishing_asks_in_a_sheet_that_counts_the_sets_not_logged(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press");
        var session = await phone.StartSessionAsync(template);
        await phone.LogSetAsync(phone.Page.Locator(".rows.working .set-row").First, "60", "10");
        await phone.Page.Locator(".set-row.done").WaitForAsync();

        await phone.Page.Locator(".topbar-trailing button[type=submit]").TapAsync();
        var sheet = phone.Page.Locator("#confirm-sheet");
        await sheet.Locator(".sheet-panel").WaitForAsync();
        (await sheet.Locator("[data-confirm-text]").TextContentAsync()).Should().Contain("2 planned sets aren't logged");

        await sheet.Locator("[data-sheet-close]").TapAsync();
        await sheet.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Hidden });
        phone.Page.Url.Should().EndWith($"/train/sessions/{session}", "Cancel keeps the workout going");

        await phone.Page.Locator(".topbar-trailing button[type=submit]").TapAsync();
        await phone.NavigateByAsync(() => sheet.Locator("[data-confirm-yes]").TapAsync());
        phone.Page.Url.Should().EndWith($"/train/sessions/{session}/summary");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Archiving_a_template_asks_first(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press");
        await phone.GoAsync($"/train/templates/{template}");

        await phone.Page.Locator(".topbar-trailing [data-menu-trigger]").TapAsync();
        await phone.Page.Locator(".topbar-trailing form[action$='/archive'] button").TapAsync();
        var sheet = phone.Page.Locator("#confirm-sheet");
        (await sheet.Locator("[data-confirm-yes]").TextContentAsync())!.Trim().Should().Be("Archive");
        await phone.NavigateByAsync(() => sheet.Locator("[data-confirm-yes]").TapAsync());
        phone.Page.Url.Should().EndWith("/train/templates");
        phone.ShouldHaveNoErrors();
    }
}
