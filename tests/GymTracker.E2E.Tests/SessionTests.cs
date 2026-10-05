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

    private static ILocator Warmups(IPage page, int card = 0) => page.Locator(".exercise").Nth(card).Locator(".rows.warmups .set-row");

    /// <summary>Drags a row's content to the left, as a finger does, to reveal its action button.</summary>
    private static async Task SwipeLeftAsync(IPage page, ILocator row)
    {
        var box = await Phone.BoxAsync(row.Locator(".set-n"));
        var y = box.Y + box.Height / 2;
        await page.Mouse.MoveAsync(box.X + 200, y);
        await page.Mouse.DownAsync();
        for (var dx = 20; dx <= 160; dx += 20)
        {
            await page.Mouse.MoveAsync(box.X + 200 - dx, y);
        }

        await page.Mouse.UpAsync();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Added_rows_and_values_typed_into_them_survive_a_reload(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press");
        await phone.StartSessionAsync(template);
        (await Rows(phone.Page).CountAsync()).Should().Be(3);

        await phone.TapAsync(".exercise .ex-actions [data-add-slot='working']");
        await Rows(phone.Page).Nth(3).WaitForAsync();
        await phone.TapAsync(".exercise .ex-actions [data-add-slot='warmup']");
        await Warmups(phone.Page).First.WaitForAsync();
        await Rows(phone.Page).Nth(1).Locator("input[name=weightKg]").FillAsync("70");

        await phone.GoAsync(phone.Page.Url);
        (await Rows(phone.Page).CountAsync()).Should().Be(4);
        (await Warmups(phone.Page).CountAsync()).Should().Be(1);
        (await Rows(phone.Page).Nth(1).Locator("input[name=weightKg]").InputValueAsync()).Should().Be("70");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task An_empty_row_can_be_swiped_away_and_stays_gone(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press");
        await phone.StartSessionAsync(template);

        await SwipeLeftAsync(phone.Page, Rows(phone.Page).Nth(2));
        await Rows(phone.Page).Nth(2).Locator(".set-delete").TapAsync();
        await phone.Page.WaitForFunctionAsync("() => document.querySelectorAll('.exercise .rows.working .set-row').length === 2");

        await phone.GoAsync(phone.Page.Url);
        (await Rows(phone.Page).CountAsync()).Should().Be(2);
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Set_numbers_stay_consecutive_after_a_set_is_deleted(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press");
        await phone.StartSessionAsync(template);
        await phone.LogSetAsync(Rows(phone.Page).Nth(0), "60", "10");
        await phone.Page.Locator(".rows.working .set-row.done").Nth(0).WaitForAsync();
        await phone.LogSetAsync(Rows(phone.Page).Nth(1), "60", "9");
        await phone.Page.Locator(".rows.working .set-row.done").Nth(1).WaitForAsync();

        await SwipeLeftAsync(phone.Page, Rows(phone.Page).Nth(1));
        await Rows(phone.Page).Nth(1).Locator(".set-delete").TapAsync();
        await phone.Page.WaitForFunctionAsync("() => document.querySelectorAll('.rows.working .set-row.done').length === 1");

        var numbers = await Rows(phone.Page).Locator(".set-n").AllInnerTextsAsync();
        numbers.Select(n => n.Trim()).Should().Equal("1", "2", "3");
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
