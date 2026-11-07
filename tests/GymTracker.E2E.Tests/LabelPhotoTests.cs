using FluentAssertions;
using Microsoft.Playwright;

namespace GymTracker.E2E.Tests;

/// <summary>Label photos in the browser: shrink, read, fill the form, share (SPEC 6.6, Phase 13).</summary>
public class LabelPhotoTests(E2EFixture app) : IClassFixture<E2EFixture>
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
        await phone.Page.RunAndWaitForRequestAsync(() => PickAsync(phone, "label.jpg"),
            r => r.Method == "POST" && r.Url.EndsWith("/food/label"));
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

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_failed_read_shows_the_message_and_keeps_what_was_typed(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync(Manual);
        await phone.Page.FillAsync("#m-name", "no label");
        await phone.Page.FillAsync("#m-kcal", "123");
        await phone.Page.FillAsync("#m-grams", "80");

        await PickAsync(phone, "label.jpg");

        await phone.Page.Locator("#toast", new() { HasText = "Couldn't find a nutrition table in that photo." }).WaitForAsync();
        (await phone.Page.InputValueAsync("#m-kcal")).Should().Be("123");
        (await phone.Page.InputValueAsync("#m-grams")).Should().Be("80");
        (await phone.Page.InputValueAsync("#m-name")).Should().Be("no label");
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task An_unticked_share_choice_survives_a_label_photo(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync($"/food/barcode?date={Today}&meal=Lunch&code={NewEan()}");
        await phone.Page.Locator("[data-label-form] input.switch[name=share]").UncheckAsync();

        await PickAsync(phone, "label.jpg");
        await FilledAsync(phone);

        (await phone.Page.Locator("[data-label-form] input.switch[name=share]").IsCheckedAsync()).Should().BeFalse();
        phone.ShouldHaveNoErrors();
    }

    [Fact]
    public async Task Three_photos_near_2_MB_each_get_through_Kestrels_request_limit()
    {
        await using var phone = await app.SignedInPhoneAsync(E2EFixture.Chromium, ai: true);
        await phone.GoAsync(Manual);

        var status = await phone.Page.EvaluateAsync<int>("""
            async () => {
              const data = new FormData();
              for (let i = 0; i < 3; i++) {
                const bytes = new Uint8Array(1_950_000);
                bytes.set([0xFF, 0xD8, 0xFF]);
                data.append("photo", new Blob([bytes], { type: "image/jpeg" }), `label${i}.jpg`);
                data.append("width", "2000");
                data.append("height", "1500");
              }
              for (const [k, v] of [["date", document.querySelector("[name=date]").value], ["meal", "Lunch"], ["barcodeField", "true"]]) data.set(k, v);
              const r = await fetch("/food/label", { method: "POST", body: data, headers: { "RequestVerificationToken": window.gymToken(), "HX-Request": "true" } });
              return r.status;
            }
            """);

        status.Should().Be(200);
    }

    [Fact]
    public async Task A_photo_between_1_and_2_MB_gets_through_Kestrels_request_limit()
    {
        // Program.cs caps request bodies at 1 MB in Kestrel; /food/label raises it to 7 MB (SPEC 9). The test server
        // in the Web tests doesn't apply Kestrel's limit, so this goes through the real server.
        await using var phone = await app.SignedInPhoneAsync(E2EFixture.Chromium, ai: true);
        await phone.GoAsync(Manual);

        var status = await phone.Page.EvaluateAsync<int>("""
            async () => {
              const bytes = new Uint8Array(1_500_000);
              bytes.set([0xFF, 0xD8, 0xFF]);
              const data = new FormData();
              data.append("photo", new Blob([bytes], { type: "image/jpeg" }), "label.jpg");
              for (const [k, v] of [["date", document.querySelector("[name=date]").value], ["meal", "Lunch"], ["barcodeField", "true"], ["width", "2000"], ["height", "1500"]]) data.set(k, v);
              const r = await fetch("/food/label", { method: "POST", body: data, headers: { "RequestVerificationToken": window.gymToken(), "HX-Request": "true" } });
              return r.status;
            }
            """);

        status.Should().Be(200);
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task You_ate_typed_before_the_photo_is_kept(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync(Manual);
        await phone.Page.FillAsync("#m-grams", "80");

        await PickAsync(phone, "label.jpg");
        await FilledAsync(phone);

        (await phone.Page.InputValueAsync("#m-grams")).Should().Be("80");
        phone.ShouldHaveNoErrors();
    }

    private static ILocator Panel(Phone phone, string scope = "[data-label-form]") => phone.Page.Locator($"{scope} [data-label-panel]");

    private static Task PickIntoSlotAsync(Phone phone, int slot, string file = "label.jpg", string scope = "[data-label-form]") =>
        phone.Page.Locator($"{scope} [data-label-slot-index=\"{slot}\"] [data-label-slot-photo]").SetInputFilesAsync(Fixture(file));

    private static Task ThumbAsync(Phone phone, int slot, string scope = "[data-label-form]") =>
        phone.Page.Locator($"{scope} [data-label-slot-index=\"{slot}\"] [data-label-thumb] img").WaitForAsync();

    [Theory]
    [InlineData(E2EFixture.Chromium, false)]
    [InlineData(E2EFixture.WebKit, false)]
    [InlineData(E2EFixture.Chromium, true)]
    public async Task The_extra_slots_open_on_a_press_and_fit_the_phone(string browser, bool bulgarian)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.WithTargetsAsync(app, bulgarian);
        await phone.GoAsync(Manual);
        (await Panel(phone).IsVisibleAsync()).Should().BeFalse();

        var more = phone.Page.Locator("[data-label-form] [data-label-more]");
        (await Phone.BoxAsync(more)).Height.Should().BeGreaterThanOrEqualTo(44);
        await more.TapAsync();

        (await Panel(phone).IsVisibleAsync()).Should().BeTrue();
        (await phone.Page.Locator("[data-label-form] [data-label-single]").IsVisibleAsync()).Should().BeFalse();
        (await phone.Page.Locator("[data-label-form] [data-label-read]").IsDisabledAsync()).Should().BeTrue();
        for (var i = 0; i < 3; i++)
        {
            var box = await Phone.BoxAsync(phone.Page.Locator($"[data-label-form] [data-label-slot-index=\"{i}\"] .label-slot-pick"));
            box.Height.Should().BeGreaterThanOrEqualTo(44);
            (box.X + box.Width).Should().BeLessThanOrEqualTo(390);
        }
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Photos_in_the_slots_are_read_together_when_read_is_pressed(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync(Manual);
        await phone.Page.Locator("[data-label-form] [data-label-more]").TapAsync();

        await PickIntoSlotAsync(phone, 0);
        await ThumbAsync(phone, 0);
        await PickIntoSlotAsync(phone, 1);
        await ThumbAsync(phone, 1);
        (await phone.Page.Locator("[data-label-form] .banner").CountAsync()).Should().Be(0, "picking into a slot sends nothing");

        await phone.Page.Locator("[data-label-form] [data-label-read]").TapAsync();
        await FilledAsync(phone);

        (await phone.Page.InputValueAsync("#m-name")).Should().Be("Айрян Верея");
        (await phone.Page.InputValueAsync("#m-serving")).Should().Be("250");
        (await Panel(phone).IsVisibleAsync()).Should().BeFalse("a read closes the slots");
        await phone.NavigateByAsync(() => phone.Page.Locator("[data-label-form] button[type=submit]").TapAsync());
        new Uri(phone.Page.Url).AbsolutePath.Should().Be("/food");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    public async Task A_removed_photo_is_left_out(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync(Manual);
        await phone.Page.Locator("[data-label-form] [data-label-more]").TapAsync();
        await PickIntoSlotAsync(phone, 0);
        await PickIntoSlotAsync(phone, 2);
        await ThumbAsync(phone, 2);

        await phone.Page.Locator("[data-label-form] [data-label-slot-index=\"2\"] [data-label-remove]").TapAsync();
        await phone.Page.Locator("[data-label-form] [data-label-read]").TapAsync();
        await FilledAsync(phone);

        (await phone.Page.InputValueAsync("#m-name")).Should().Be("Айрян", "only one photo was sent");
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    public async Task Use_one_photo_closes_the_slots_and_drops_them(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync(Manual);
        var more = phone.Page.Locator("[data-label-form] [data-label-more]");
        await more.TapAsync();
        await PickIntoSlotAsync(phone, 1);
        await ThumbAsync(phone, 1);

        await phone.Page.Locator("[data-label-form] [data-label-less]").TapAsync();
        (await phone.Page.Locator("[data-label-form] [data-label-single]").IsVisibleAsync()).Should().BeTrue();
        (await Panel(phone).IsVisibleAsync()).Should().BeFalse();

        await more.TapAsync();
        (await phone.Page.Locator("[data-label-form] [data-label-thumb] img").CountAsync()).Should().Be(0);
        (await phone.Page.Locator("[data-label-form] [data-label-read]").IsDisabledAsync()).Should().BeTrue();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_failed_read_keeps_the_slots_filled(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync(Manual);
        // The stub reads no table when the typed name is "no label".
        await phone.Page.FillAsync("#m-name", "no label");
        await phone.Page.Locator("[data-label-form] [data-label-more]").TapAsync();
        await PickIntoSlotAsync(phone, 0);
        await PickIntoSlotAsync(phone, 1);
        await ThumbAsync(phone, 1);

        await phone.Page.Locator("[data-label-form] [data-label-read]").TapAsync();
        await phone.Page.Locator("#toast", new() { HasText = "Couldn't find a nutrition table in that photo." }).WaitForAsync();

        (await phone.Page.Locator("[data-label-form] [data-label-thumb] img").CountAsync()).Should().Be(2);
        (await phone.Page.Locator("[data-label-form] [data-label-read]").IsEnabledAsync()).Should().BeTrue();
        (await phone.Page.InputValueAsync("#m-name")).Should().Be("no label");
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    public async Task Slots_and_read_are_disabled_while_reading(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync(Manual);
        // The stub answers slowly when the typed name is "slow".
        await phone.Page.FillAsync("#m-name", "slow");
        await phone.Page.Locator("[data-label-form] [data-label-more]").TapAsync();
        await PickIntoSlotAsync(phone, 0);
        await ThumbAsync(phone, 0);

        await phone.Page.Locator("[data-label-form] [data-label-read]").TapAsync();

        (await phone.Page.Locator("[data-label-form] [data-label-read]").IsDisabledAsync()).Should().BeTrue();
        (await phone.Page.Locator("[data-label-form] [data-label-slot-index=\"1\"] [data-label-slot-photo]").IsDisabledAsync()).Should().BeTrue();
        (await phone.Page.Locator("[data-label-form] [data-label-less]").IsDisabledAsync()).Should().BeTrue("the photos are being read");
        await FilledAsync(phone);
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    public async Task A_photo_still_shrinking_when_the_slots_close_doesnt_come_back(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync(Manual);
        await phone.Page.EvaluateAsync("""
            () => {
                const shrink = window.gymImage.shrink;
                window.gymImage.shrink = async (...args) => {
                    await new Promise((done) => setTimeout(done, 800));
                    return shrink.apply(window.gymImage, args);
                };
            }
            """);
        var more = phone.Page.Locator("[data-label-form] [data-label-more]");
        await more.TapAsync();

        await PickIntoSlotAsync(phone, 1);
        await phone.Page.Locator("[data-label-form] [data-label-less]").TapAsync();
        await phone.Page.WaitForTimeoutAsync(1500);
        await more.TapAsync();

        (await phone.Page.Locator("[data-label-form] [data-label-thumb] img").CountAsync()).Should().Be(0);
        (await phone.Page.Locator("[data-label-form] [data-label-read]").IsDisabledAsync()).Should().BeTrue();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    public async Task Slot_thumbnails_are_freed_when_a_new_read_replaces_their_form(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=barcode");
        await phone.Page.Locator("input[data-label-start]").SetInputFilesAsync(Fixture("label.jpg"));
        await phone.Page.Locator("[data-label-slot] .banner").WaitForAsync();
        await phone.Page.EvaluateAsync("""
            () => {
                window.revoked = [];
                const revoke = URL.revokeObjectURL;
                URL.revokeObjectURL = (url) => { window.revoked.push(url); revoke.call(URL, url); };
            }
            """);
        await phone.Page.Locator("[data-label-slot] [data-label-more]").TapAsync();
        await PickIntoSlotAsync(phone, 0, scope: "[data-label-slot]");
        await ThumbAsync(phone, 0, "[data-label-slot]");
        var thumb = await phone.Page.Locator("[data-label-slot] [data-label-thumb] img").GetAttributeAsync("src");

        await phone.Page.Locator("input[data-label-start]").SetInputFilesAsync(Fixture("label.jpg"));
        await phone.Page.WaitForFunctionAsync("() => !document.querySelector('[data-label-slot] [data-label-thumb] img')");

        (await phone.Page.EvaluateAsync<string[]>("() => window.revoked")).Should().Contain(thumb);
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    public async Task A_photo_the_browser_cannot_open_leaves_its_slot_empty(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync(Manual);
        await phone.Page.Locator("[data-label-form] [data-label-more]").TapAsync();

        await PickIntoSlotAsync(phone, 0, "not-an-image.jpg");

        await phone.Page.Locator("#toast", new() { HasText = "Couldn't read that picture. Try another photo." }).WaitForAsync();
        (await phone.Page.Locator("[data-label-form] [data-label-thumb] img").CountAsync()).Should().Be(0);
        (await phone.Page.Locator("[data-label-form] [data-label-read]").IsDisabledAsync()).Should().BeTrue();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task More_photos_on_the_scan_tab_open_the_filled_form_below(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=barcode");
        const string scan = "[data-label-photos]:not([data-label-form] *)";
        await phone.Page.Locator($"{scan} [data-label-more]").TapAsync();
        (await phone.Page.Locator($"{scan} [data-label-single]").IsVisibleAsync()).Should().BeFalse();
        (await phone.Page.Locator("[data-start]").IsVisibleAsync()).Should().BeTrue("Scan a barcode stays");

        await PickIntoSlotAsync(phone, 0, scope: scan);
        await PickIntoSlotAsync(phone, 1, scope: scan);
        await ThumbAsync(phone, 1, scan);
        await phone.Page.Locator($"{scan} [data-label-read]").TapAsync();
        await phone.Page.Locator("[data-label-slot] .banner").WaitForAsync();

        (await phone.Page.InputValueAsync("[data-label-slot] #m-serving")).Should().Be("250");
        (await Panel(phone, scan).IsVisibleAsync()).Should().BeFalse();
        new Uri(phone.Page.Url).AbsolutePath.Should().Be("/food/add");
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    public async Task A_read_replaced_by_another_doesnt_leave_the_scan_tab_spinning(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=barcode");
        await phone.Page.Locator("input[data-label-start]").SetInputFilesAsync(Fixture("label.jpg"));
        await phone.Page.Locator("[data-label-slot] .banner").WaitForAsync();

        // The next read answers late, so the one after it replaces it while it is still running.
        var calls = 0;
        await phone.Page.RouteAsync("**/food/label", async route =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await Task.Delay(2000);
            }

            try
            {
                await route.ContinueAsync();
            }
            catch (PlaywrightException)
            {
                // The page aborted it.
            }
        });
        const string scan = "[data-label-photos]:not([data-label-form] *)";
        await phone.Page.Locator($"{scan} [data-label-more]").TapAsync();
        await PickIntoSlotAsync(phone, 0, scope: scan);
        await ThumbAsync(phone, 0, scan);
        await phone.Page.Locator($"{scan} [data-label-read]").TapAsync();
        await phone.Page.Locator("[data-module=\"label\"].is-busy").WaitForAsync();

        await phone.Page.Locator("[data-label-slot] [data-label-single] input").SetInputFilesAsync(Fixture("label.jpg"));
        await phone.Page.WaitForFunctionAsync("() => !document.querySelector('[data-label-slot] .is-busy') && document.querySelector('[data-label-slot] .banner')");
        await phone.Page.WaitForTimeoutAsync(2500);

        (await phone.Page.Locator("[data-module=\"label\"].is-busy").CountAsync()).Should().Be(0, "the replaced read's spinner is cleared");
        (await phone.Page.Locator("[data-label-status]").IsVisibleAsync()).Should().BeFalse();
    }
}
