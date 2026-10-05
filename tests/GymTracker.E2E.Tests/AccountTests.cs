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
}
