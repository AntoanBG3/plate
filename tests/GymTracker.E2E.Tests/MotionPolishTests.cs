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
}
