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
                return {
                    text: t.textContent.trim(), left: box.left, right: box.right, height: box.height,
                    labelWidth: label.width, labelHeight: label.height, lineHeight: parseFloat(getComputedStyle(t).lineHeight),
                    // What a finger lands on 20 px above and below the middle of the word.
                    tall: [middle - 20, middle + 20].every((y) => t.contains(document.elementFromPoint(box.left + box.width / 2, y))),
                };
            })
            """);

        tabs.Should().HaveCount(5);
        foreach (var tab in tabs)
        {
            tab.LabelWidth.Should().BeLessThanOrEqualTo(tab.Right - tab.Left + 0.5, $"\"{tab.Text}\" fits in its tab");
            tab.LabelHeight.Should().BeLessThan(tab.LineHeight * 1.5, $"\"{tab.Text}\" stays on one line");
            tab.Height.Should().BeGreaterThanOrEqualTo(44, $"\"{tab.Text}\" is tall enough to tap");
            tab.Tall.Should().BeTrue($"\"{tab.Text}\" takes a tap anywhere in its 44 px");
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

    // Playwright fills this from the page's object, so it needs settable properties and no constructor.
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
    }
}
