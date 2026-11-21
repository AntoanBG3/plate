using FluentAssertions;

namespace Plate.E2E.Tests;

/// <summary>
/// The five ways to add food are tabs on one page: words with a bar under the selected one. Switching changes the
/// page's contents in place; the title and the tabs hold still and the bar glides.
/// </summary>
public class ModeTabsTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    [Theory]
    [InlineData(E2EFixture.Chromium, 320, false)]
    [InlineData(E2EFixture.Chromium, 320, true)]
    [InlineData(E2EFixture.Chromium, 390, false)]
    [InlineData(E2EFixture.Chromium, 390, true)]
    [InlineData(E2EFixture.WebKit, 320, false)]
    [InlineData(E2EFixture.WebKit, 320, true)]
    [InlineData(E2EFixture.WebKit, 390, false)]
    [InlineData(E2EFixture.WebKit, 390, true)]
    public async Task Every_mode_fits_on_one_line_and_is_easy_to_tap(string browser, int width, bool bulgarian)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, width);
        await phone.WithTargetsAsync(app, bulgarian);

        await phone.GoAsync("/food/add");

        var tabs = await phone.Page.EvaluateAsync<TabBox[]>("""
            () => [...document.querySelectorAll(".mode-tabs .mode-tab")].map((t) => {
                const box = t.getBoundingClientRect();
                const label = t.querySelector("span").getBoundingClientRect();
                const middle = box.top + box.height / 2;
                const centre = label.left + label.width / 2;
                const mine = (x, y) => t.contains(document.elementFromPoint(Math.min(Math.max(x, 1), innerWidth - 1), y));
                return {
                    text: t.textContent.trim(), left: box.left, right: box.right, height: box.height,
                    labelWidth: label.width, labelHeight: label.height, lineHeight: parseFloat(getComputedStyle(t).lineHeight),
                    // What a finger lands on 21 px above, below and to either side of the middle of the word.
                    tall: [middle - 21, middle + 21].every((y) => mine(centre, y)),
                    wide: [centre - 21, centre + 21].every((x) => mine(x, middle)),
                    gapAfter: t.nextElementSibling ? t.nextElementSibling.getBoundingClientRect().left - box.right : 0,
                };
            })
            """);

        tabs.Should().HaveCount(5);
        foreach (var tab in tabs)
        {
            tab.LabelWidth.Should().BeLessThanOrEqualTo(tab.Right - tab.Left + 0.5, $"\"{tab.Text}\" fits in its tab");
            tab.LabelHeight.Should().BeLessThan(tab.LineHeight * 1.5, $"\"{tab.Text}\" stays on one line");
            tab.Height.Should().BeGreaterThanOrEqualTo(44, $"\"{tab.Text}\" is tall enough to tap");
            tab.Tall.Should().BeTrue($"\"{tab.Text}\" takes a tap anywhere in its 44 px of height");
            tab.Wide.Should().BeTrue($"\"{tab.Text}\" takes a tap 21 px to either side of its middle");
            tab.GapAfter.Should().BeLessThan(1, $"no tap lands between \"{tab.Text}\" and the next tab");
        }
        tabs.Zip(tabs.Skip(1)).Should().OnlyContain(pair => pair.First.Right <= pair.Second.Left + 0.5, "the words don't run into each other");
        tabs[0].Left.Should().BeGreaterThanOrEqualTo(0);
        tabs[^1].Right.Should().BeLessThanOrEqualTo(width, "the row fits the screen");
        (await phone.Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth")).Should().BeTrue("nothing scrolls sideways");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Another_mode_changes_in_place_and_the_bar_glides(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/food/add");
        var titleBefore = await Phone.BoxAsync(phone.Page.Locator(".large-title"));
        var tabsBefore = await Phone.BoxAsync(phone.Page.Locator(".mode-tabs"));
        // The kind of page change, and what the browser animates on its own during it.
        await phone.Page.EvaluateAsync("""
            () => document.addEventListener("gym:page", (e) => {
                window.__change = { nav: e.detail.nav, names: [] };
                requestAnimationFrame(() => {
                    window.__change.names = document.getAnimations()
                        .map((a) => a.effect && a.effect.pseudoElement || "")
                        .filter((p) => p.startsWith("::view-transition-group"));
                });
            }, { once: true })
            """);

        await phone.NavigateByAsync(() => phone.TapAsync(".mode-tabs a[href*='tab=recent']"));

        (await phone.Page.EvaluateAsync<string>("window.__change.nav")).Should().Be("swap", "another mode is the same page with other contents");
        (await phone.Page.EvaluateAsync<string[]>("window.__change.names")).Should().Contain("::view-transition-group(mode-bar)", "the bar moves on its own to the new word");
        phone.Page.Url.Should().Contain("tab=recent");
        (await phone.Page.Locator(".mode-tabs .mode-tab[aria-current='page']").GetAttributeAsync("href")).Should().Contain("tab=recent");
        (await phone.Page.Locator(".mode-tabs .mode-bar").CountAsync()).Should().Be(1, "one bar, under the selected mode");
        var titleAfter = await Phone.BoxAsync(phone.Page.Locator(".large-title"));
        var tabsAfter = await Phone.BoxAsync(phone.Page.Locator(".mode-tabs"));
        titleAfter.Y.Should().Be(titleBefore.Y);
        tabsAfter.Y.Should().Be(tabsBefore.Y, "the tabs hold still while the contents change");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task The_bar_travels_with_its_page_when_the_page_is_pushed(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/food");
        await phone.Page.EvaluateAsync("""
            () => document.addEventListener("gym:page", (e) => {
                window.__change = { nav: e.detail.nav, name: getComputedStyle(document.querySelector(".mode-bar")).viewTransitionName };
            }, { once: true })
            """);

        await phone.NavigateByAsync(() => phone.TapAsync("#main a[href^='/food/add']"));

        (await phone.Page.EvaluateAsync<string>("window.__change.nav")).Should().Be("forward");
        (await phone.Page.EvaluateAsync<string>("window.__change.name")).Should().Be("none", "a bar with its own transition would hang in place while its page slides in");
        phone.ShouldHaveNoErrors();
    }

    private const string WatchNav = "() => document.addEventListener('gym:page', (e) => { window.__nav = e.detail.nav; }, { once: true })";

    private static string Today() =>
        Plate.Core.Time.UserClock.Today(DateTimeOffset.UtcNow, Plate.Core.Domain.UserProfile.DefaultTimeZoneId).ToString("yyyy-MM-dd");

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_read_that_fails_comes_back_in_place_at_the_same_address(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync($"/food/add?date={Today()}&meal=Lunch");
        var address = phone.Page.Url;
        var titleBefore = await Phone.BoxAsync(phone.Page.Locator(".large-title"));
        var entries = await phone.Page.EvaluateAsync<int>("history.length");
        // Longer than the server accepts; the field's own limit only stops typing.
        await phone.Page.EvaluateAsync("() => { document.getElementById('text').value = 'a'.repeat(1001); }");
        await phone.Page.EvaluateAsync(WatchNav);

        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/parse'] button[type=submit]").TapAsync());

        (await phone.Page.EvaluateAsync<string>("window.__nav")).Should().Be("same", "the page it was sent from is shown again, with the error");
        (await phone.Page.Locator(".banner.is-error").IsVisibleAsync()).Should().BeTrue();
        phone.Page.Url.Should().Be(address, "the form's own address answers a POST only");
        (await phone.Page.EvaluateAsync<int>("history.length")).Should().Be(entries);
        (await phone.Page.InputValueAsync("#text")).Should().HaveLength(1001, "nothing typed is lost");
        (await Phone.BoxAsync(phone.Page.Locator(".large-title"))).Y.Should().Be(titleBefore.Y);
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_read_that_works_goes_on_to_the_next_page(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync($"/food/add?date={Today()}&meal=Lunch");
        await phone.Page.FillAsync("#text", "a banana");
        await phone.Page.EvaluateAsync(WatchNav);

        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/parse'] button[type=submit]").TapAsync());

        (await phone.Page.Locator(".result").CountAsync()).Should().BeGreaterThan(0);
        (await phone.Page.EvaluateAsync<string>("window.__nav")).Should().NotBe("same").And.NotBe("swap", "the list to confirm is another page");
        new Uri(phone.Page.Url).AbsolutePath.Should().StartWith("/food/review/");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Back_after_trying_other_modes_leaves_the_page(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/food");
        await phone.NavigateByAsync(() => phone.TapAsync("#main a[href^='/food/add']"));
        await phone.NavigateByAsync(() => phone.TapAsync(".mode-tabs a[href*='tab=search']"));
        await phone.NavigateByAsync(() => phone.TapAsync(".mode-tabs a[href*='tab=recent']"));
        phone.Page.Url.Should().Contain("tab=recent");

        await phone.NavigateByAsync(() => phone.Page.GoBackAsync());

        new Uri(phone.Page.Url).AbsolutePath.Should().Be("/food", "a mode is a choice on the page, not a step to go back through");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Wrong_digits_come_back_in_place_after_a_full_load(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        // Every kind of page change each document is given, from its first moment.
        await phone.Context.AddInitScriptAsync("""
            window.__navs = [];
            new MutationObserver(() => {
                const nav = document.documentElement && document.documentElement.dataset.nav;
                const bar = document.querySelector(".mode-bar");
                if (nav) { window.__navs.push(nav + (bar ? " bar=" + getComputedStyle(bar).viewTransitionName : "")); }
            }).observe(document, { subtree: true, attributes: true, attributeFilter: ["data-nav"] });
            """);
        await phone.GoAsync($"/food/add?date={Today()}&meal=Lunch&tab=barcode");
        await phone.Page.FillAsync("#code", "12345678");

        await phone.NavigateByAsync(() => phone.Page.Locator("#barcode-form button[type=submit]").TapAsync());

        new Uri(phone.Page.Url).AbsolutePath.Should().Be("/food/barcode");
        (await phone.Page.Locator(".field-error").IsVisibleAsync()).Should().BeTrue();
        // Browsers without transitions between documents show the page at once, which is in place too.
        var navs = await phone.Page.EvaluateAsync<string[]>("window.__navs");
        navs.Should().NotContain(n => n != "swap bar=none", "the Scan tab shown again is not a new place, and its bar has no partner in the page that was left");
        // A browser may show the new document without a transition, so the choice itself is checked as well: the
        // page that was left is still on record, and the reveal is replayed with a transition that never ends.
        var replay = await phone.Page.EvaluateAsync<string>("""
            () => {
                const reveal = new Event("pagereveal");
                reveal.viewTransition = { ready: Promise.resolve(), finished: new Promise(() => {}) };
                window.dispatchEvent(reveal);
                return document.documentElement.dataset.nav + " bar=" + getComputedStyle(document.querySelector(".mode-bar")).viewTransitionName;
            }
            """);
        replay.Should().Be("swap bar=none");
        phone.ShouldHaveNoErrors();
    }

    // Playwright fills this from the page's object, so it needs settable properties and no constructor.
    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Another_mode_changes_in_place_from_an_error_shown_at_another_address(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        // Digits that aren't a barcode come back as the Scan tab, at the lookup's address.
        // Yesterday (UTC) is never a future day in the user's time zone, which the add page would refuse.
        await phone.GoAsync($"/food/barcode?date={DateTime.UtcNow.AddDays(-1):yyyy-MM-dd}&meal=Lunch&code=12345678");
        (await phone.Page.Locator(".mode-tabs .mode-tab[aria-current='page']").GetAttributeAsync("href")).Should().Contain("tab=barcode");
        await phone.Page.EvaluateAsync("() => document.addEventListener('gym:page', (e) => { window.__nav = e.detail.nav; }, { once: true })");

        await phone.NavigateByAsync(() => phone.TapAsync(".mode-tabs a[href*='tab=manual']"));

        (await phone.Page.EvaluateAsync<string>("window.__nav")).Should().Be("swap");
        new Uri(phone.Page.Url).AbsolutePath.Should().Be("/food/add");
        phone.ShouldHaveNoErrors();
    }

    private sealed class TabBox
    {
        public string Text { get; set; } = "";
        public double Left { get; set; }
        public double Right { get; set; }
        public double Height { get; set; }
        public double LabelWidth { get; set; }
        public double LabelHeight { get; set; }
        public double LineHeight { get; set; }
        public bool Tall { get; set; }
        public bool Wide { get; set; }
        public double GapAfter { get; set; }
    }
}
