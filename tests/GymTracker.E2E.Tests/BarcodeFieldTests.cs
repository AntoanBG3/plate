using FluentAssertions;
using Microsoft.Playwright;

namespace GymTracker.E2E.Tests;

/// <summary>The scan button beside the manual form's barcode field (SPEC 7, Phase 15).</summary>
[Collection(E2ECollection.Name)]
public class BarcodeFieldTests(E2EFixture app)
{
    private static string Today => GymTracker.Core.Time.UserClock.Today(DateTimeOffset.UtcNow, GymTracker.Core.Domain.UserProfile.DefaultTimeZoneId).ToString("yyyy-MM-dd");

    private static string Fixture(string name) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", name));

    /// <summary>A random valid EAN-13, so runs never collide on shared foods.</summary>
    internal static string NewEan()
    {
        var digits = Enumerable.Range(0, 12).Select(i => i == 0 ? Random.Shared.Next(1, 10) : Random.Shared.Next(10)).ToArray();
        var sum = digits.Select((d, i) => i % 2 == 0 ? d : d * 3).Sum();
        return string.Concat(digits) + (10 - sum % 10) % 10;
    }

    /// <summary>Taps the scan button and answers the sheet with a photo of the code, as the "Take a photo instead" fallback does.</summary>
    internal static async Task ScanIntoFieldAsync(Phone phone, string code, string scope = "")
    {
        await phone.Page.Locator($"{scope} [data-scan-into]".Trim()).First.TapAsync();
        await phone.Page.Locator("#scanner:not([hidden])").WaitForAsync();
        await phone.Page.Locator("#scanner [data-photo]").SetInputFilesAsync(new FilePayload
        {
            Name = "barcode.png", MimeType = "image/png", Buffer = UntestedAreasTests.Ean13Png(code),
        });
        await phone.Page.WaitForFunctionAsync("code => document.querySelector('[data-barcode-input]')?.value === code", code);
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task The_scan_button_fills_the_manual_forms_barcode_field(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=manual");
        var button = phone.Page.Locator("[data-scan-into]");
        var box = await Phone.BoxAsync(button);
        box.Width.Should().BeGreaterThanOrEqualTo(44);
        box.Height.Should().BeGreaterThanOrEqualTo(44);
        (box.X + box.Width).Should().BeLessThanOrEqualTo(390);
        var code = NewEan();

        await ScanIntoFieldAsync(phone, code);

        new Uri(phone.Page.Url).AbsolutePath.Should().Be("/food/add");
        await phone.Page.Locator("#scanner").WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        (await phone.Page.Locator("form [data-share]").First.IsVisibleAsync()).Should().BeTrue();
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task The_scan_button_works_in_the_form_from_photograph_a_label(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=barcode");
        await phone.Page.Locator("[data-label-start]").SetInputFilesAsync(Fixture("label.jpg"));
        await phone.Page.Locator("[data-label-slot] .banner").WaitForAsync();
        var code = NewEan();

        await ScanIntoFieldAsync(phone, code, "[data-label-slot]");

        new Uri(phone.Page.Url).AbsolutePath.Should().Be("/food/add");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task The_share_row_follows_the_barcode_field_without_the_label_model(string browser)
    {
        // label.js doesn't load without a label model; the share row must still appear (Review Focus 2).
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=manual");
        (await phone.Page.Locator("form [data-share]").First.IsVisibleAsync()).Should().BeFalse();

        await phone.Page.FillAsync("#m-barcode", NewEan());

        (await phone.Page.Locator("form [data-share]").First.IsVisibleAsync()).Should().BeTrue();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_scanned_code_the_account_already_saved_is_offered_and_yes_opens_it(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var code = NewEan();
        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=manual");
        await SaveManualAsync(phone, "Known kefir e2e", code, share: false);

        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=manual");
        await ScanIntoFieldAsync(phone, code);
        var card = phone.Page.Locator("[data-known-food]");
        await card.WaitForAsync();
        (await card.InnerTextAsync()).Should().Contain("Known kefir e2e").And.Contain("Is this the same food?");

        await phone.NavigateByAsync(() => card.Locator("a", new() { HasText = "Yes, use it" }).TapAsync());
        new Uri(phone.Page.Url).AbsolutePath.Should().Be("/food/barcode");
        (await phone.Page.Locator("main").InnerTextAsync()).Should().Contain("Known kefir e2e");
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Rejecting_a_shared_match_unticks_share_and_saves(string browser)
    {
        var code = NewEan();
        await using (var other = await app.SignedInPhoneAsync(browser))
        {
            await other.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=manual");
            await SaveManualAsync(other, "Shared boza e2e", code, share: true);
        }

        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=manual");
        await phone.Page.FillAsync("#m-name", "My boza e2e");
        await ScanIntoFieldAsync(phone, code);
        await phone.Page.Locator("[data-known-food]").WaitForAsync();
        (await phone.Page.Locator("[data-known-food]").InnerTextAsync()).Should().Contain("Added by another user");

        await phone.Page.Locator("[data-known-reject]").TapAsync();

        (await phone.Page.Locator("form input.switch[name=share]").IsCheckedAsync()).Should().BeFalse();
        (await phone.Page.InputValueAsync("#m-barcode")).Should().Be(code);
        (await phone.Page.Locator("[data-known]").InnerTextAsync()).Should().Contain("kept for you only");
        await FillNumbersAsync(phone);
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/items'] button[type=submit]").TapAsync());
        new Uri(phone.Page.Url).AbsolutePath.Should().Be("/food");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    public async Task Rejecting_your_own_match_clears_the_barcode(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var code = NewEan();
        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=manual");
        await SaveManualAsync(phone, "Own ayran e2e", code, share: false);

        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=manual");
        await ScanIntoFieldAsync(phone, code);
        await phone.Page.Locator("[data-known-reject]").TapAsync();

        (await phone.Page.InputValueAsync("#m-barcode")).Should().BeEmpty();
        (await phone.Page.Locator("form [data-share]").First.IsVisibleAsync()).Should().BeFalse();
    }

    private static async Task FillNumbersAsync(Phone phone)
    {
        await phone.Page.FillAsync("#m-kcal", "60");
        await phone.Page.FillAsync("#m-protein", "3");
        await phone.Page.FillAsync("#m-carbs", "4");
        await phone.Page.FillAsync("#m-fat", "3");
        await phone.Page.FillAsync("#m-grams", "200");
    }

    /// <summary>Saves a manual food with a barcode through the form, as a user would.</summary>
    private static async Task SaveManualAsync(Phone phone, string name, string code, bool share)
    {
        await phone.Page.FillAsync("#m-name", name);
        await phone.Page.FillAsync("#m-barcode", code);
        await FillNumbersAsync(phone);
        await phone.Page.Locator("form input.switch[name=share]").SetCheckedAsync(share);
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/items'] button[type=submit]").TapAsync());
    }
}
