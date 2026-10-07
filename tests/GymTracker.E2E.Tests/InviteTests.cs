using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Web.Features.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymTracker.E2E.Tests;

/// <summary>Joining from an invite link on a phone (SPEC 9, Phase 12).</summary>
[Collection(E2ECollection.Name)]
public class InviteTests(E2EFixture app)
{
    private async Task<string> InviteAsync()
    {
        await using (var db = app.Db.CreateContext(null))
        {
            // The open-invite cap is global; each test starts without invites.
            await db.Invites.ExecuteDeleteAsync();
        }

        using var scope = app.App.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<InviteAdmin>().CreateAsync(null, "Ivan from the gym", 7, default)).Token;
    }

    [Theory]
    [InlineData(E2EFixture.Chromium, 390, "en-GB", "Create your account", Language.English)]
    [InlineData(E2EFixture.Chromium, 320, "bg-BG", "Създай профила си", Language.Bulgarian)]
    [InlineData(E2EFixture.WebKit, 390, "bg-BG", "Създай профила си", Language.Bulgarian)]
    [InlineData(E2EFixture.WebKit, 320, "en-GB", "Create your account", Language.English)]
    public async Task Joining_from_a_link_in_the_phones_language_lands_on_the_setup_chat(string browser, int width, string locale, string title, Language language)
    {
        var token = await InviteAsync();
        await using var phone = await app.PhoneAsync(browser, width, locale);
        await phone.GoAsync($"/join/{token}");

        (await phone.Page.Locator("h1").InnerTextAsync()).Should().Be(title);
        (await phone.Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"))
            .Should().BeTrue("nothing runs off the side at this width");
        foreach (var target in await phone.Page.Locator(".auth-lang a, .auth-form button").AllAsync())
        {
            (await target.BoundingBoxAsync())!.Height.Should().BeGreaterThanOrEqualTo(44, "touch targets are at least 44 px");
        }

        var email = $"{Guid.NewGuid():N}@test.local";
        await phone.Page.FillAsync("#name", "Ivan");
        await phone.Page.FillAsync("#email", email);
        await phone.Page.FillAsync("#password", "correct horse battery");
        await phone.Page.Locator(".auth-form button[type=submit]").TapAsync();
        await phone.Page.WaitForURLAsync(url => url.Contains("/welcome"));

        await using var db = app.Db.CreateContext(null);
        var id = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        (await db.Profiles.IgnoreQueryFilters().SingleAsync(p => p.UserId == id)).Language.Should().Be(language);
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task The_language_switch_reloads_the_page_in_the_other_language(string browser)
    {
        var token = await InviteAsync();
        await using var phone = await app.PhoneAsync(browser);
        await phone.GoAsync($"/join/{token}");

        await phone.NavigateByAsync(() => phone.Page.Locator(".auth-lang a[lang=bg]").TapAsync());

        (await phone.Page.Locator("h1").InnerTextAsync()).Should().Be("Създай профила си");
        (await phone.Page.Locator(".auth-lang a[aria-current]").InnerTextAsync()).Should().Be("Български");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_taken_email_shows_the_reason_and_keeps_what_was_typed(string browser)
    {
        var token = await InviteAsync();
        var (taken, _) = await app.App.CreateUserAsync();
        await using var phone = await app.PhoneAsync(browser, 390, "bg-BG");
        await phone.GoAsync($"/join/{token}");

        await phone.Page.FillAsync("#name", "Иван");
        await phone.Page.FillAsync("#email", taken);
        await phone.Page.FillAsync("#password", "correct horse battery");
        await phone.NavigateByAsync(() => phone.Page.Locator(".auth-form button[type=submit]").TapAsync());

        (await phone.Page.Locator(".banner.is-error").InnerTextAsync()).Should().Contain("Вече има профил с този имейл.");
        (await phone.Page.InputValueAsync("#name")).Should().Be("Иван");
        (await phone.Page.InputValueAsync("#email")).Should().Be(taken);
        phone.Page.Url.Should().Contain($"/join/{token}");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_used_link_shows_the_message_and_no_form(string browser)
    {
        await using var phone = await app.PhoneAsync(browser, 320);
        await phone.GoAsync($"/join/{InviteRules.NewToken().Token}");

        (await phone.Page.Locator(".banner").InnerTextAsync()).Should().Contain("This invite is no longer valid.");
        (await phone.Page.Locator("form.auth-form").CountAsync()).Should().Be(0);
        phone.ShouldHaveNoErrors();
    }
}
