using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace GymTracker.E2E.Tests;

/// <summary>Motion refinements taken from apple.com (docs/plans/2026-10-11-motion-polish-design.md).</summary>
public class MotionPolishTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    private const string MenuVisibility = "m => getComputedStyle(m).visibility";

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_closed_menu_cannot_be_focused_and_Escape_returns_to_its_button(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press", "Dip");
        await phone.GoAsync($"/train/templates/{template}");
        var menu = phone.Page.Locator(".topbar-trailing .menu");
        var trigger = phone.Page.Locator(".topbar-trailing [data-menu-trigger]");

        (await menu.EvaluateAsync<string>(MenuVisibility)).Should().Be("hidden");
        (await menu.EvaluateAsync<bool>("m => { const item = m.querySelector('[role=menuitem]'); item.focus(); return document.activeElement === item; }"))
            .Should().BeFalse("a closed menu's items must not take focus or be read out");

        // Opened from the keyboard, the menu focuses its first item (motion.js, click with detail 0).
        await trigger.FocusAsync();
        await phone.Page.Keyboard.PressAsync("Enter");
        (await menu.EvaluateAsync<string>(MenuVisibility)).Should().Be("visible");
        (await menu.EvaluateAsync<bool>("m => m.contains(document.activeElement)")).Should().BeTrue();

        await phone.Page.Keyboard.PressAsync("Escape");
        await phone.Page.WaitForFunctionAsync("() => getComputedStyle(document.querySelector('.topbar-trailing .menu')).visibility === 'hidden'");
        (await trigger.EvaluateAsync<bool>("t => document.activeElement === t")).Should().BeTrue("closing returns focus to the menu's button");
        phone.ShouldHaveNoErrors();
    }

    // A short screen, so the Body section (trend value and sparkline) on Today starts below the fold.
    private const int ShortScreen = 480;

    /// <summary>Opens <paramref name="path"/> as if for the first time this session, so first-sight animations play.</summary>
    private static async Task FirstSightAsync(Phone phone, string path)
    {
        await phone.Page.EvaluateAsync("() => sessionStorage.clear()");
        await phone.GoAsync(path);
    }

    /// <summary>Five weigh-ins up to today, enough for a trend and a sparkline on Today.</summary>
    private async Task SeedWeightsAsync(Phone phone)
    {
        await using var db = app.Db.CreateContext(null);
        var id = await db.Users.Where(u => u.Email == phone.Email).Select(u => u.Id).SingleAsync();
        var today = UserClock.Today(DateTimeOffset.UtcNow, UserProfile.DefaultTimeZoneId);
        for (var i = 0; i < 5; i++)
        {
            db.Bodyweights.Add(new BodyweightEntry { UserId = id, Date = today.AddDays(-i), WeightKg = 80m + i * 0.3m });
        }

        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Below_the_fold_animations_wait_until_scrolled_into_view(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await SeedWeightsAsync(phone);
        await phone.Page.SetViewportSizeAsync(390, ShortScreen);
        await FirstSightAsync(phone, "/today");

        var spark = phone.Page.Locator(".spark-line");
        (await spark.EvaluateAsync<bool>("e => e.getBoundingClientRect().top > innerHeight")).Should().BeTrue("the test needs the sparkline below the fold");
        (await spark.EvaluateAsync<int>("e => e.getAnimations().length")).Should().Be(0, "the line draws in when it is seen, not at load");

        await spark.ScrollIntoViewIfNeededAsync();
        await phone.Page.WaitForFunctionAsync("() => document.querySelector('.spark-line').getAnimations().length > 0");
        await phone.Page.WaitForFunctionAsync("() => { const v = document.querySelector('#main section:has(.spark) [data-count]'); return v.textContent.trim() === v.dataset.count; }");
        phone.ShouldHaveNoErrors();
    }

    // A guard: it passes before and after this work, and keeps reduced motion from ever hiding content.
    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Reduced_motion_hides_nothing(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await SeedWeightsAsync(phone);
        await phone.Page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        await phone.Page.SetViewportSizeAsync(390, ShortScreen);

        foreach (var path in new[] { "/today", "/progress" })
        {
            await FirstSightAsync(phone, path);
            (await phone.Page.Locator(".is-pending-rise, .is-rising").CountAsync()).Should().Be(0, $"nothing on {path} waits to rise in");
            (await phone.Page.EvaluateAsync<bool>("() => [...document.querySelectorAll('[data-count]')].every(e => e.textContent.trim() === e.dataset.count)"))
                .Should().BeTrue($"every number on {path} shows its value at once");
        }

        phone.ShouldHaveNoErrors();
    }

    private const string BodySection = "#main > section:has(.spark)";

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Blocks_below_the_fold_rise_in_once(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await SeedWeightsAsync(phone);
        await phone.Page.SetViewportSizeAsync(390, ShortScreen);
        await FirstSightAsync(phone, "/today");

        var body = phone.Page.Locator(BodySection);
        (await body.EvaluateAsync<bool>("e => e.getBoundingClientRect().top >= innerHeight")).Should().BeTrue("the test needs the Body section below the fold");
        (await body.GetAttributeAsync("class")).Should().Contain("is-pending-rise");
        (await body.EvaluateAsync<string>("e => getComputedStyle(e).opacity")).Should().Be("0");

        await body.ScrollIntoViewIfNeededAsync();
        await phone.Page.WaitForFunctionAsync($$"""
            () => { const b = document.querySelector("{{BodySection}}");
                    return !b.classList.contains("is-pending-rise") && !b.classList.contains("is-rising") && getComputedStyle(b).opacity === "1"; }
            """);

        await phone.GoAsync("/today");
        (await phone.Page.Locator(".is-pending-rise").CountAsync()).Should().Be(0, "a page rises in on its first sight in a session only");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Leaving_a_page_with_blocks_still_waiting_leaves_nothing_hidden_on_screen(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await SeedWeightsAsync(phone);
        await phone.Page.SetViewportSizeAsync(390, ShortScreen);
        await FirstSightAsync(phone, "/today");
        (await phone.Page.Locator(".is-pending-rise").CountAsync()).Should().BeGreaterThan(0);

        await phone.NavigateByAsync(() => phone.Page.Locator(".tabbar a[href='/food']").TapAsync());
        (await phone.Page.EvaluateAsync<bool>("() => [...document.querySelectorAll('.is-pending-rise')].every(e => e.getBoundingClientRect().top >= innerHeight)"))
            .Should().BeTrue("only blocks below the fold may wait, invisible, to rise in");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_screen_taller_than_the_page_hides_nothing(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.Page.SetViewportSizeAsync(390, 3000);
        await FirstSightAsync(phone, "/today");
        (await phone.Page.Locator(".is-pending-rise").CountAsync()).Should().Be(0);
        phone.ShouldHaveNoErrors();
    }

    private const string CaloriesChartRunning = "() => Chart.animator.running(Chart.getChart(document.getElementById('calories-chart')))";

    /// <summary>A target and one day of food, so Progress has a Calories chart (lower on the page).</summary>
    private async Task WithCaloriesAsync(Phone phone)
    {
        await phone.WithTargetsAsync(app);
        await phone.GoAsync("/today"); // stores today's estimate, the first day with a target
        await phone.GoAsync("/food");
        var quick = await phone.Page.Locator("[data-sheet-open='quick-sheet']").First.GetAttributeAsync("hx-get");
        var date = System.Text.RegularExpressions.Regex.Match(quick!, @"date=([0-9-]+)").Groups[1].Value;
        await phone.PostAsync("/food/quick", new() { ["date"] = date, ["meal"] = "Lunch", ["kcal"] = "600", ["protein"] = "40" });
    }

    private static Task TwoFramesAsync(Phone phone) =>
        phone.Page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))");

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_chart_below_the_fold_draws_in_when_scrolled_to(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await WithCaloriesAsync(phone);
        await phone.Page.SetViewportSizeAsync(390, ShortScreen);
        await FirstSightAsync(phone, "/progress");
        await phone.Page.WaitForFunctionAsync("() => window.Chart && Chart.getChart(document.getElementById('calories-chart'))");

        var canvas = phone.Page.Locator("#calories-chart");
        (await canvas.EvaluateAsync<bool>("e => e.getBoundingClientRect().top > innerHeight")).Should().BeTrue("the test needs the chart below the fold");
        (await phone.Page.EvaluateAsync<bool>(CaloriesChartRunning)).Should().BeFalse("the chart draws in when it is seen, not at load");

        await canvas.ScrollIntoViewIfNeededAsync();
        await phone.Page.WaitForFunctionAsync(CaloriesChartRunning);
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_theme_change_before_a_chart_is_seen_redraws_it_quietly(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await WithCaloriesAsync(phone);
        await phone.Page.SetViewportSizeAsync(390, ShortScreen);
        await FirstSightAsync(phone, "/progress");
        await phone.Page.WaitForFunctionAsync("() => window.Chart && Chart.getChart(document.getElementById('calories-chart'))");

        await phone.Page.EvaluateAsync("() => { document.documentElement.dataset.theme = 'light'; }");
        await TwoFramesAsync(phone);
        await phone.Page.Locator("#calories-chart").ScrollIntoViewIfNeededAsync();
        await TwoFramesAsync(phone);
        (await phone.Page.EvaluateAsync<bool>(CaloriesChartRunning)).Should().BeFalse("a redraw for the theme has no entrance");
        phone.ShouldHaveNoErrors();
    }
}
