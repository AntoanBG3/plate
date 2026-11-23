using FluentAssertions;

namespace Plate.E2E.Tests;

/// <summary>Destructive and final actions ask in the app's own sheet, never the browser's confirm (SPEC 7).</summary>
public class ConfirmSheetTests(E2EFixture app) : IClassFixture<E2EFixture>
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
        await phone.AnimationsDoneAsync("#confirm-sheet .sheet-panel");
        await phone.NavigateByAsync(() => sheet.Locator("[data-confirm-yes]").TapAsync());
        phone.Page.Url.Should().EndWith("/train/templates");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task The_confirmation_opens_on_top_of_another_sheet(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var today = Plate.Core.Time.UserClock.Today(DateTimeOffset.UtcNow, Plate.Core.Domain.UserProfile.DefaultTimeZoneId).ToString("yyyy-MM-dd");
        await phone.GoAsync("/food");
        await phone.PostAsync("/food/quick", new() { ["date"] = today, ["meal"] = "Lunch", ["description"] = "Wrap", ["kcal"] = "600" });
        await phone.PostAsync("/food/saved-meals", new() { ["date"] = today, ["meal"] = "Lunch", ["name"] = "Usual lunch" });
        await phone.GoAsync($"/food/add?date={today}&meal=Dinner&tab=recent");
        await phone.Page.Locator("button.row-link", new() { HasText = "Usual lunch" }).TapAsync();
        await phone.Page.Locator("#food-sheet-body form[action$='/delete'] button[type=submit]").TapAsync();

        // The confirmation's button must be the element under its own centre, not the food sheet behind it.
        var yes = phone.Page.Locator("#confirm-sheet [data-confirm-yes]");
        await yes.WaitForAsync();
        await phone.AnimationsDoneAsync("#confirm-sheet .sheet-panel");
        (await yes.EvaluateAsync<bool>("b => { const r = b.getBoundingClientRect(); return document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2)?.closest('[data-confirm-yes]') === b; }"))
            .Should().BeTrue("the confirmation sits above the sheet that asked for it");
        await phone.Page.Keyboard.PressAsync("Escape");
        var delete = phone.Page.Locator("#food-sheet-body form[action$='/delete'] button[type=submit]");
        (await delete.EvaluateAsync<bool>("b => b === document.activeElement")).Should().BeTrue();
    }

    [Fact]
    public async Task Quick_sheet_keeps_tab_inside_the_dialog()
    {
        await using var phone = await app.SignedInPhoneAsync(E2EFixture.Chromium);
        await phone.GoAsync("/food");
        await phone.Page.Locator("[data-sheet-open='quick-sheet']").First.TapAsync();
        var save = phone.Page.Locator("#quick-sheet button[type=submit]");
        await save.WaitForAsync();
        // htmx focuses the form's autofocus field when the swap settles, 20 ms after the content appears. Moving focus
        // before then lost it to that field on CI; the empty panel already holds focus as the sheet opens, so "focus is
        // in the sheet" was true too early.
        await phone.Page.WaitForFunctionAsync("() => document.activeElement?.id === 'quick-kcal'");
        await save.FocusAsync();
        await phone.Page.Keyboard.PressAsync("Tab");
        (await phone.Page.EvaluateAsync<bool>("() => document.activeElement.matches('#quick-sheet [data-sheet-close]')")).Should().BeTrue();
        await phone.Page.Keyboard.PressAsync("Shift+Tab");
        (await save.EvaluateAsync<bool>("b => b === document.activeElement")).Should().BeTrue();
        await phone.Page.Keyboard.PressAsync("Tab");
        await phone.Page.Keyboard.PressAsync("Escape");
        (await phone.Page.Locator("[data-sheet-open='quick-sheet']").First.EvaluateAsync<bool>("b => b === document.activeElement")).Should().BeTrue();
    }
}
