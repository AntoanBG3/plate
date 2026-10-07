using FluentAssertions;
using Microsoft.Playwright;

namespace GymTracker.E2E.Tests;

/// <summary>Label photos in the browser: shrink, read, fill the form, share (SPEC 6.6, Phase 13).</summary>
[Collection(E2ECollection.Name)]
public class LabelPhotoTests(E2EFixture app)
{
    private const string Manual = "/food/add?meal=Lunch&tab=manual";
    private const string Filled = "Read from the photo. Check each value against the label.";

    private static string Today => GymTracker.Core.Time.UserClock.Today(DateTimeOffset.UtcNow, GymTracker.Core.Domain.UserProfile.DefaultTimeZoneId).ToString("yyyy-MM-dd");

    private static string Fixture(string name) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", name));

    /// <summary>A random valid EAN-13, so runs never collide on shared foods.</summary>
    private static string NewEan()
    {
        var digits = Enumerable.Range(0, 12).Select(i => i == 0 ? Random.Shared.Next(1, 10) : Random.Shared.Next(10)).ToArray();
        var sum = digits.Select((d, i) => i % 2 == 0 ? d : d * 3).Sum();
        return string.Concat(digits) + (10 - sum % 10) % 10;
    }

    private static async Task PickAsync(Phone phone, string file, string selector = "[data-label-form] [data-label-photo]") =>
        await phone.Page.Locator(selector).First.SetInputFilesAsync(Fixture(file));

    private static Task FilledAsync(Phone phone) =>
        phone.Page.Locator(".banner", new() { HasText = "Read from the photo" }).WaitForAsync();

    [Theory]
    [InlineData(E2EFixture.Chromium, false)]
    [InlineData(E2EFixture.WebKit, false)]
    [InlineData(E2EFixture.Chromium, true)]
    public async Task A_label_photo_fills_the_manual_form_and_saves(string browser, bool bulgarian)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.WithTargetsAsync(app, bulgarian);
        await phone.GoAsync(Manual);

        await PickAsync(phone, "label.jpg");
        await phone.Page.Locator("[data-label-form] .banner").First.WaitForAsync();

        (await phone.Page.InputValueAsync("#m-kcal")).Should().Be("38");
        (await phone.Page.InputValueAsync("#m-protein")).Should().Be("1.8");
        (await phone.Page.InputValueAsync("#m-name")).Should().Be("Айрян");
        (await phone.Page.Locator("[data-label-form] .banner").First.InnerTextAsync()).Should().Contain(bulgarian ? "100 ml" : "Per 100 ml, used as per 100 g.");

        await phone.Page.FillAsync("#m-grams", "250");
        await phone.NavigateByAsync(() => phone.Page.Locator("[data-label-form] button[type=submit]").TapAsync());
        new Uri(phone.Page.Url).AbsolutePath.Should().Be("/food");
        (await phone.Page.Locator("main").InnerTextAsync()).Should().Contain("Айрян");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task After_a_barcode_miss_the_photo_fills_the_form_and_the_food_is_shared_by_default(string browser)
    {
        var code = NewEan();
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync($"/food/barcode?date={Today}&meal=Lunch&code={code}");

        await PickAsync(phone, "label.jpg");
        await FilledAsync(phone);
        (await phone.Page.Locator("[data-label-form] .list[data-share]").IsVisibleAsync()).Should().BeTrue();
        await phone.Page.FillAsync("#m-grams", "250");
        await phone.NavigateByAsync(() => phone.Page.Locator("[data-label-form] button[type=submit]").TapAsync());

        await using var other = await app.SignedInPhoneAsync(browser, ai: true);
        await other.GoAsync($"/food/barcode?date={Today}&meal=Lunch&code={code}");
        var page = await other.Page.Locator("main").InnerTextAsync();
        page.Should().Contain("Айрян").And.Contain("Added by another user").And.NotContain(phone.Email);
        phone.ShouldHaveNoErrors();
        other.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Photograph_a_label_on_the_scan_tab_opens_the_filled_form_with_the_barcode_from_the_photo(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync("/food/add?meal=Lunch&tab=barcode");

        await PickAsync(phone, "label-with-barcode.jpg", "[data-label-start]");
        await FilledAsync(phone);

        (await phone.Page.InputValueAsync("[data-label-slot] #m-barcode")).Should().Be("5901234123457");
        (await phone.Page.Locator("[data-label-slot] .list[data-share]").IsVisibleAsync()).Should().BeTrue();
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_file_the_browser_cannot_open_leaves_the_form_alone(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync(Manual);
        await phone.Page.FillAsync("#m-kcal", "123");

        await PickAsync(phone, "not-an-image.jpg");

        await phone.Page.Locator("#toast", new() { HasText = "Couldn't read that picture" }).WaitForAsync();
        (await phone.Page.InputValueAsync("#m-kcal")).Should().Be("123");
        (await phone.Page.Locator("[data-label] .thinking").IsVisibleAsync()).Should().BeFalse();
        phone.Requests.Should().NotContain(r => r.StartsWith("POST /food/label"));
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_second_photo_replaces_a_first_one_still_being_read(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync(Manual);

        await phone.Page.FillAsync("#m-name", "slow");
        await PickAsync(phone, "label.jpg");
        await phone.Page.WaitForRequestAsync(r => r.Url.EndsWith("/food/label"));
        await phone.Page.FillAsync("#m-name", "");
        await PickAsync(phone, "label.jpg");

        await FilledAsync(phone);
        await phone.Page.WaitForTimeoutAsync(2000); // past the slow answer
        (await phone.Page.InputValueAsync("#m-name")).Should().Be("Айрян");
        (await phone.Page.Locator(".banner", new() { HasText = "Read from the photo" }).CountAsync()).Should().Be(1);
        (await phone.Page.Locator("#toast").InnerTextAsync()).Should().NotContain("Couldn't read the label");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_typed_name_is_kept_when_the_label_fills_the_numbers(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync(Manual);
        await phone.Page.FillAsync("#m-name", "Моят айрян");

        await PickAsync(phone, "label.jpg");
        await FilledAsync(phone);

        (await phone.Page.InputValueAsync("#m-name")).Should().Be("Моят айрян");
        (await phone.Page.InputValueAsync("#m-kcal")).Should().Be("38");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Offline_the_photo_button_is_disabled(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync(Manual);

        await phone.Context.SetOfflineAsync(true);
        await phone.Page.Locator("[data-label-photo]:disabled").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        (await phone.Page.Locator("[data-label-button]").GetAttributeAsync("title")).Should().Be("Needs a connection");

        await phone.Context.SetOfflineAsync(false);
        await phone.Page.Locator("[data-label-photo]:enabled").WaitForAsync(new() { State = WaitForSelectorState.Attached });
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task The_share_choice_follows_the_barcode_field(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync(Manual);
        var share = phone.Page.Locator(".list[data-share]");

        (await share.IsVisibleAsync()).Should().BeFalse();
        await phone.Page.FillAsync("#m-barcode", "5901234123457");
        (await share.IsVisibleAsync()).Should().BeTrue();
        await phone.Page.FillAsync("#m-barcode", "");
        (await share.IsVisibleAsync()).Should().BeFalse();
        phone.ShouldHaveNoErrors();
    }
}
