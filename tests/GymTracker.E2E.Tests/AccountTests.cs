using FluentAssertions;

namespace GymTracker.E2E.Tests;

/// <summary>Sign-in, setup and Settings forms (Phase 11).</summary>
[Collection(E2ECollection.Name)]
public class AccountTests(E2EFixture app)
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
        (await phone.Page.EvaluateAsync<string>("() => document.activeElement.id")).Should().Be("password");
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
}
