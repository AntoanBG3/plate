using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Plate.E2E.Tests;

/// <summary>Sign-in, setup and Settings forms (Phase 11).</summary>
public class AccountTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_wrong_password_keeps_the_email_and_puts_the_cursor_in_the_password(string browser)
    {
        var (email, _) = await app.App.CreateUserAsync();
        await using var phone = await app.PhoneAsync(browser);
        await phone.GoAsync("/account/login");
        await phone.Page.FillAsync("#email", email);
        await phone.Page.FillAsync("#password", "not the password");
        await phone.Page.ClickAsync("button[type=submit]");
        await phone.Page.Locator(".banner.is-error").WaitForAsync();

        (await phone.Page.InputValueAsync("#email")).Should().Be(email);
        // Focus can land a frame after the error shows (WebKit under load); it must land on the password.
        await phone.Page.WaitForFunctionAsync("() => document.activeElement && document.activeElement.id === 'password'", null, new() { Timeout = 5000 });
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Setup_starts_on_maintain_and_asks_for_a_rate_only_for_a_cut_or_bulk(string browser)
    {
        var (email, password) = await app.App.CreateUserAsync(onboarded: false);
        await using var phone = await app.PhoneAsync(browser);
        await phone.SignInAsync(email, password);
        await phone.GoAsync("/welcome");
        var rate = phone.Page.Locator("[name=targetRatePercentPerWeek]");

        (await phone.Page.IsCheckedAsync("input[name=goal][value=Maintain]")).Should().BeTrue();
        (await rate.IsVisibleAsync()).Should().BeFalse();
        (await rate.IsDisabledAsync()).Should().BeTrue("a hidden field must not block the form as required");

        await phone.Page.Locator("label.segment:has(input[value=Cut])").TapAsync();
        (await rate.IsVisibleAsync()).Should().BeTrue();
        (await rate.GetAttributeAsync("placeholder")).Should().Be("0.5");
        (await rate.EvaluateAsync<bool>("e => e.required")).Should().BeTrue();

        await phone.Page.Locator("label.segment:has(input[value=Bulk])").TapAsync();
        (await rate.GetAttributeAsync("placeholder")).Should().Be("0.25");

        // One visible "Goal": the section title. The radio group is named by aria-label only.
        (await phone.Page.Locator("form :text-is('Goal'):visible").CountAsync()).Should().Be(1);
        phone.ShouldHaveNoErrors();
    }

    private async Task<Plate.Core.Domain.UserProfile> ProfileAsync(Phone phone)
    {
        await using var db = app.Db.CreateContext(null);
        var userId = await db.Users.Where(u => u.Email == phone.Email).Select(u => u.Id).SingleAsync();
        return await db.Profiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.UserId == userId);
    }

    private static Task SavedAsync(Phone phone, string form = "#profile-form") =>
        phone.Page.Locator($"{form}.is-saved").WaitForAsync();

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Settings_save_on_change_and_say_so_under_the_card(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.WithTargetsAsync(app);
        await phone.GoAsync("/settings");
        await phone.NavigateByAsync(() => phone.Page.Locator("a[href='/settings/profile']").TapAsync());
        (await phone.Page.Locator("#profile-form button[type=submit]:visible").CountAsync()).Should().Be(0, "there is no Save button");

        await phone.Page.FillAsync("#heightCm", "176");
        await phone.Page.Locator("#heightCm").BlurAsync();
        await SavedAsync(phone);
        (await ProfileAsync(phone)).HeightCm.Should().Be(176);

        // "Saved" shows right under the Profile card, not at the bottom of the page.
        var status = await phone.Page.EvaluateAsync<bool>(
            "() => document.querySelector('#heightCm').closest('.list').nextElementSibling.matches('.autosave-status')");
        status.Should().BeTrue();

        await phone.GoAsync("/settings/goal");
        await phone.Page.Locator(".choice-row:has(input[value=Active])").TapAsync();
        await phone.Page.WaitForFunctionAsync("() => document.querySelector('#goal-form.is-saved') && !document.querySelector('#goal-form.is-dirty')");
        await phone.Page.WaitForTimeoutAsync(300);
        (await ProfileAsync(phone)).ActivityLevel.Should().Be(Plate.Core.Domain.ActivityLevel.Active);
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_cut_waits_for_its_rate_and_a_refused_value_shows_why(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.WithTargetsAsync(app);
        await phone.GoAsync("/settings/goal");

        await phone.Page.Locator("label.segment:has(input[name=goal][value=Cut])").TapAsync();
        var error = phone.Page.Locator("[data-autosave-error]");
        await error.WaitForAsync();
        (await error.InnerTextAsync()).Should().Contain("Enter a weekly rate for a cut or bulk.");
        (await ProfileAsync(phone)).Goal.Should().Be(Plate.Core.Domain.Goal.Maintain, "nothing is saved without a rate");

        await phone.Page.FillAsync("#rate", "0.5");
        await phone.Page.Locator("#rate").BlurAsync();
        await SavedAsync(phone, "#goal-form");
        (await error.IsHiddenAsync()).Should().BeTrue();
        var saved = await ProfileAsync(phone);
        saved.Goal.Should().Be(Plate.Core.Domain.Goal.Cut);
        saved.TargetRatePercentPerWeek.Should().Be(0.5m);

        // The browser's own range check comes first (it stopped this too, with its message); without it, the server's
        // refusal shows under the field, not as a toast, and isn't retried.
        await phone.GoAsync("/settings/profile");
        await phone.Page.EvalOnSelectorAsync("#heightCm", "e => e.removeAttribute('max')");
        await phone.Page.FillAsync("#heightCm", "300");
        await phone.Page.Locator("#heightCm").BlurAsync();
        await phone.Page.Locator("#profile-form.has-error").WaitForAsync();
        (await error.InnerTextAsync()).Should().Contain("Height must be between");
        (await phone.Page.Locator(".toast:not([hidden])").CountAsync()).Should().Be(0);
        (await ProfileAsync(phone)).HeightCm.Should().Be(180);
    }
}
