using FluentAssertions;
using Microsoft.Playwright;

namespace Plate.E2E.Tests;

/// <summary>The Add to Home Screen card on Today and the guide in Settings (SPEC 8), per browser.</summary>
public class InstallHintTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    private const string IPhoneSafari = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.5 Mobile/15E148 Safari/604.1";
    private const string IPhoneInstagram = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/22F76 Instagram 380.0.0.28.104 (iPhone15,2; iOS 18_5; en_GB)";
    private const string AndroidChrome = "Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Mobile Safari/537.36";

    /// <summary>A signed-in phone whose browser reports <paramref name="userAgent"/> to scripts (the server sees the real one).</summary>
    private async Task<Phone> PhoneAsync(string browser, string? userAgent, bool standalone = false, string locale = "en-GB")
    {
        var phone = await app.SignedInPhoneAsync(browser, locale: locale);
        if (userAgent is not null)
        {
            await phone.Context.AddInitScriptAsync($"Object.defineProperty(Navigator.prototype, 'userAgent', {{ get: () => {System.Text.Json.JsonSerializer.Serialize(userAgent)}, configurable: true }});");
        }
        if (standalone)
        {
            await phone.Context.AddInitScriptAsync("Object.defineProperty(Navigator.prototype, 'standalone', { get: () => true, configurable: true });");
        }
        return phone;
    }

    private static ILocator Card(Phone phone) => phone.Page.Locator("[data-module=install-card]");

    private static Task<string[]> ShownSectionsAsync(Phone phone) =>
        phone.Page.EvaluateAsync<string[]>("() => [...document.querySelectorAll('[data-module=install-guide] > [data-platform]')].filter((s) => !s.hidden).map((s) => s.dataset.platform)");

    [Theory]
    [InlineData(E2EFixture.Chromium, false)]
    [InlineData(E2EFixture.WebKit, true)]
    public async Task Safari_on_an_iPhone_gets_the_card_until_Not_now(string browser, bool bulgarian)
    {
        await using var phone = await PhoneAsync(browser, IPhoneSafari, locale: bulgarian ? "bg-BG" : "en-GB");
        await phone.WithTargetsAsync(app, bulgarian);
        await phone.GoAsync("/today");

        await Card(phone).WaitForAsync(new() { State = WaitForSelectorState.Visible });
        (await Card(phone).Locator("[data-install-prompt]").IsHiddenAsync()).Should().BeTrue("Safari has no install prompt to open");
        var overflow = await phone.Page.EvaluateAsync<double>("() => document.documentElement.scrollWidth - window.innerWidth");
        overflow.Should().BeLessThanOrEqualTo(0, "the card fits the phone");

        await phone.NavigateByAsync(() => Card(phone).Locator("[data-install-fallback]").TapAsync(), "/settings/install");
        (await ShownSectionsAsync(phone)).Should().Equal("ios-safari");

        await phone.GoAsync("/today");
        await Card(phone).Locator("[data-install-dismiss]").TapAsync();
        (await Card(phone).IsHiddenAsync()).Should().BeTrue();
        await phone.GoAsync("/today");
        (await Card(phone).IsHiddenAsync()).Should().BeTrue("Not now lasts on this device");

        // The guide stays one row away in Settings.
        await phone.GoAsync("/settings");
        await phone.NavigateByAsync(() => phone.Page.Locator("main a[href='/settings/install']").TapAsync(), "/settings/install");
        (await ShownSectionsAsync(phone)).Should().Equal("ios-safari");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task The_installed_app_shows_no_card(string browser)
    {
        await using var phone = await PhoneAsync(browser, IPhoneSafari, standalone: true);
        await phone.GoAsync("/today");
        (await Card(phone).IsHiddenAsync()).Should().BeTrue();

        await phone.GoAsync("/settings/install");
        (await ShownSectionsAsync(phone)).Should().Equal("standalone");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_computer_shows_no_card_and_points_to_the_phone(string browser)
    {
        await using var phone = await PhoneAsync(browser, userAgent: null);
        await phone.GoAsync("/today");
        (await Card(phone).IsHiddenAsync()).Should().BeTrue();

        await phone.GoAsync("/settings/install");
        (await ShownSectionsAsync(phone)).Should().Equal("other");
        phone.ShouldHaveNoErrors();
    }

    [Fact]
    public async Task An_in_app_browser_is_told_to_open_Safari_or_Chrome()
    {
        await using var phone = await PhoneAsync(E2EFixture.WebKit, IPhoneInstagram);
        await phone.GoAsync("/settings/install");
        (await ShownSectionsAsync(phone)).Should().Equal("in-app");
        phone.ShouldHaveNoErrors();
    }

    [Fact]
    public async Task Android_Chrome_installs_from_the_card()
    {
        await using var phone = await PhoneAsync(E2EFixture.Chromium, AndroidChrome);
        await phone.GoAsync("/today");
        await Card(phone).WaitForAsync(new() { State = WaitForSelectorState.Visible });
        (await Card(phone).Locator("[data-install-prompt]").IsHiddenAsync()).Should().BeTrue("no prompt has been offered yet");

        // What Chrome fires once the page qualifies; the real one opens the browser's install sheet.
        await phone.Page.EvaluateAsync("""
            () => {
              const e = new Event("beforeinstallprompt");
              e.prompt = () => { window.__prompted = true; return Promise.resolve(); };
              e.userChoice = Promise.resolve({ outcome: "accepted" });
              window.dispatchEvent(e);
            }
            """);
        await Card(phone).Locator("[data-install-prompt]").WaitForAsync(new() { State = WaitForSelectorState.Visible });
        (await Card(phone).Locator("[data-install-fallback]").IsHiddenAsync()).Should().BeTrue("Install replaces Show me how");

        await Card(phone).Locator("[data-install-prompt]").TapAsync();
        await Card(phone).WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        (await phone.Page.EvaluateAsync<bool>("() => window.__prompted === true")).Should().BeTrue();
        phone.ShouldHaveNoErrors();
    }
}
