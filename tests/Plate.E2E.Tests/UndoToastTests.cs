using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace Plate.E2E.Tests;

/// <summary>
/// The Undo toast (SPEC 7): where it sits, how long it stays, and that Undo always goes through. The toast's timer runs on
/// Playwright's clock, moved on with RunForAsync rather than waited out; the server accepts undo for minutes, not seconds.
/// </summary>
public class UndoToastTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    private static async Task<string> TodayAsync(Phone phone)
    {
        await phone.GoAsync("/food");
        var quick = await phone.Page.Locator("[data-sheet-open='quick-sheet']").First.GetAttributeAsync("hx-get");
        return System.Text.RegularExpressions.Regex.Match(quick!, @"date=([0-9-]+)").Groups[1].Value;
    }

    private async Task<int> QuickEntryAsync(Phone phone, string date, string name)
    {
        await phone.PostAsync("/food/quick", new() { ["date"] = date, ["meal"] = "Lunch", ["description"] = name, ["kcal"] = "500" });
        await using var db = app.Db.CreateContext(null);
        return await db.FoodLog.IgnoreQueryFilters().Where(e => e.Description == name).Select(e => e.Id).SingleAsync();
    }

    private async Task<bool> IsDeletedAsync(int id)
    {
        await using var db = app.Db.CreateContext(null);
        return await db.FoodLog.IgnoreQueryFilters().Where(e => e.Id == id).Select(e => e.DeletedAt != null).SingleAsync();
    }

    private static async Task PauseClockAsync(Phone phone)
    {
        await phone.Page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        var pauseAt = DateTime.UtcNow;
        await phone.Page.Clock.InstallAsync(new() { TimeDate = pauseAt.AddMinutes(-1) });
        await phone.Page.Clock.PauseAtAsync(pauseAt);
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Undo_restores_a_deleted_entry_whenever_it_is_tapped_while_shown(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var date = await TodayAsync(phone);
        await PauseClockAsync(phone);

        foreach (var delayMs in new[] { 500, 2000, 4000, 5500 })
        {
            var id = await QuickEntryAsync(phone, date, $"Undo test {delayMs} {Guid.NewGuid():N}");
            await phone.GoAsync($"/food/entries/{id}");
            await phone.NavigateByAsync(() => phone.Page.Locator("form[action$='/delete'] button").TapAsync());
            (await IsDeletedAsync(id)).Should().BeTrue();
            var button = phone.Page.Locator("#toast:not([hidden]) .toast-action button");
            await button.WaitForAsync();

            // Time spent on browser and database calls doesn't count towards the six seconds.
            await phone.Page.Clock.RunForAsync(delayMs);
            (await button.IsVisibleAsync()).Should().BeTrue($"the toast should still show {delayMs} ms in");
            var undo = await Phone.BoxAsync(button);
            await phone.NavigateByAsync(() => phone.Page.Touchscreen.TapAsync(undo.X + undo.Width / 2, undo.Y + undo.Height / 2));
            (await IsDeletedAsync(id)).Should().BeFalse($"Undo tapped {delayMs} ms after the delete should restore it");
        }

        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task The_toast_stays_while_pressed(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.Page.Clock.InstallAsync();
        var date = await TodayAsync(phone);
        var id = await QuickEntryAsync(phone, date, $"Hold test {Guid.NewGuid():N}");
        await phone.GoAsync($"/food/entries/{id}");
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action$='/delete'] button").TapAsync());

        var message = await Phone.BoxAsync(phone.Page.Locator("#toast > span"));
        await phone.Page.Mouse.MoveAsync(message.X + 10, message.Y + message.Height / 2);
        await phone.Page.Mouse.DownAsync();
        await phone.Page.Clock.RunForAsync(7000);
        // The fade out runs in real time, so a toast that did time out is only hidden once it finishes.
        await phone.AnimationsDoneAsync("#toast");
        (await phone.Page.Locator("#toast").IsHiddenAsync()).Should().BeFalse("a pressed toast doesn't time out");
        await phone.Page.Mouse.UpAsync();

        await phone.Page.Clock.RunForAsync(6000);
        await phone.Page.Locator("#toast").WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Hidden, Timeout = 2000 });
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task During_a_rest_the_toast_sits_above_the_timer_and_a_late_tap_never_skips_the_rest(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.Page.Clock.InstallAsync();
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press");
        await phone.StartSessionAsync(template);
        var row = phone.Page.Locator(".rows.working .set-row").First;
        await phone.LogSetAsync(row, "60", "10");
        await phone.Page.Locator(".rows.working .set-row.done").First.WaitForAsync();
        await phone.Page.Locator("#rest-timer:not([hidden])").WaitForAsync();

        // Delete the set: swipe it open and tap Delete.
        var box = await Phone.BoxAsync(row.Locator(".set-n"));
        await phone.Page.Mouse.MoveAsync(box.X + 200, box.Y + box.Height / 2);
        await phone.Page.Mouse.DownAsync();
        for (var dx = 20; dx <= 160; dx += 20)
        {
            await phone.Page.Mouse.MoveAsync(box.X + 200 - dx, box.Y + box.Height / 2);
        }

        await phone.Page.Mouse.UpAsync();
        await row.Locator(".set-delete").TapAsync();
        await phone.Page.Locator("#toast:not([hidden]) .toast-action").WaitForAsync();
        await phone.AnimationsDoneAsync("#toast");
        await phone.AnimationsDoneAsync("#rest-timer");

        var toast = await Phone.BoxAsync(phone.Page.Locator("#toast"));
        var timer = await Phone.BoxAsync(phone.Page.Locator("#rest-timer"));
        Phone.Overlap(toast, timer).Should().BeFalse("the toast must not cover the rest timer");

        // Where Undo was, tapped just after the toast went away.
        var undo = await Phone.BoxAsync(phone.Page.Locator("#toast .toast-action button"));
        await phone.Page.Clock.RunForAsync(6000);
        await phone.Page.Locator("#toast").WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Hidden, Timeout = 2000 });
        await phone.Page.Mouse.ClickAsync(undo.X + undo.Width / 2, undo.Y + undo.Height / 2);
        (await phone.Page.Locator("#rest-timer").IsVisibleAsync()).Should().BeTrue("a late Undo tap must not land on Skip");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task The_undo_toast_goes_away_on_another_page(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press", "Barbell row");
        await phone.GoAsync($"/train/templates/{template}");
        await phone.Page.Locator(".exercise").Nth(1).Locator("[data-menu-trigger]").TapAsync();
        await phone.NavigateByAsync(() => phone.Page.Locator(".exercise").Nth(1).Locator("form[action$='/remove'] button").TapAsync());
        await phone.Page.Locator("#toast:not([hidden]) .toast-action").WaitForAsync();

        await phone.NavigateByAsync(() => phone.TapAsync(".tabbar a[href='/train']"));
        (await phone.Page.Locator("#toast").IsHiddenAsync()).Should().BeTrue();
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_food_entry_can_be_swiped_away_and_the_totals_follow(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var date = await TodayAsync(phone);
        var id = await QuickEntryAsync(phone, date, $"Swipe test {Guid.NewGuid():N}");
        await phone.GoAsync("/food");
        (await phone.Page.Locator(".macro-main .metric-value").TextContentAsync()).Should().Be("500");

        var row = phone.Page.Locator("[data-swipe-row]").First;
        var box = await Phone.BoxAsync(row.Locator(".swipe-content"));
        await phone.Page.Mouse.MoveAsync(box.X + box.Width - 40, box.Y + box.Height / 2);
        await phone.Page.Mouse.DownAsync();
        for (var dx = 20; dx <= 160; dx += 20)
        {
            await phone.Page.Mouse.MoveAsync(box.X + box.Width - 40 - dx, box.Y + box.Height / 2);
        }

        await phone.Page.Mouse.UpAsync();
        phone.Page.Url.Should().EndWith("/food", "a swipe is not a tap on the row");
        await phone.NavigateByAsync(() => row.Locator(".set-delete").TapAsync());

        (await IsDeletedAsync(id)).Should().BeTrue();
        (await phone.Page.Locator(".macro-main .metric-value").TextContentAsync()).Should().Be("0");
        await phone.Page.Locator("#toast:not([hidden]) .toast-action").WaitForAsync();
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_tap_just_after_an_undo_toast_goes_doesnt_reach_what_was_under_it(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var date = await TodayAsync(phone);
        var id = await QuickEntryAsync(phone, date, $"Late tap test {Guid.NewGuid():N}");
        await phone.GoAsync($"/food/entries/{id}");
        await PauseClockAsync(phone);
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action$='/delete'] button").TapAsync());
        await phone.Page.Locator("#toast:not([hidden]) .toast-action").WaitForAsync();

        // A button under Undo, as Skip was under it before the toast moved above the rest timer (QA H2). The toast
        // keeping off the timer is checked above; this is the second guard, for whatever else is under a toast.
        var undo = await Phone.BoxAsync(phone.Page.Locator("#toast .toast-action button"));
        await phone.Page.EvaluateAsync("""
            (b) => {
                const under = document.createElement("button");
                under.style.cssText = `position: fixed; left: ${b.x}px; top: ${b.y}px; width: ${b.w}px; height: ${b.h}px; z-index: 1`;
                window.__underTaps = 0;
                under.addEventListener("click", () => { window.__underTaps++; });
                document.body.append(under);
            }
            """,
            // As doubles: float values don't reach the page as numbers.
            new { x = (double)undo.X, y = (double)undo.Y, w = (double)undo.Width, h = (double)undo.Height });

        // The clock stands still from the toast running out to the late tap, so the tap is "just after" on any machine.
        await phone.Page.Clock.RunForAsync(6000);
        (await phone.Page.Locator("#toast").IsHiddenAsync()).Should().BeTrue();
        var x = undo.X + undo.Width / 2;
        var y = undo.Y + undo.Height / 2;
        await phone.Page.Touchscreen.TapAsync(x, y);
        (await phone.Page.EvaluateAsync<int>("window.__underTaps")).Should().Be(0, "a tap meant for Undo, landing just after it went, is dropped");

        // Later taps there go through, so the button really was under the finger.
        await phone.Page.Clock.RunForAsync(500);
        await phone.Page.Touchscreen.TapAsync(x, y);
        (await phone.Page.EvaluateAsync<int>("window.__underTaps")).Should().Be(1);
        (await IsDeletedAsync(id)).Should().BeTrue("nothing undid the delete");
        phone.ShouldHaveNoErrors();
    }
}
