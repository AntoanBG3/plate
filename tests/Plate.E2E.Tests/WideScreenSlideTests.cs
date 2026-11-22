using FluentAssertions;

namespace Plate.E2E.Tests;

/// <summary>
/// On a screen wider than the 640 px page column, a page that slides in or out travels the whole screen. Slid by its
/// own width, the next page sat in full view beside the current one before the slide had started.
/// </summary>
public class WideScreenSlideTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    // On the next page change, the page and title snapshots that are entirely off screen where their slide starts
    // (a page coming in) or ends (a page going out).
    private const string WatchOffScreen = """
        () => document.addEventListener("gym:page", () => requestAnimationFrame(() => {
            const root = document.documentElement;
            const boxes = { page: document.getElementById("main"), "large-title": document.querySelector("#main .large-title") };
            window.__offscreen = document.getAnimations()
                .filter((a) => /^::view-transition-(old|new)\((page|large-title)\)$/.test(a.effect && a.effect.pseudoElement || ""))
                .filter((a) => {
                    const pseudo = a.effect.pseudoElement;
                    const box = boxes[pseudo.match(/\((.*)\)/)[1]];
                    if (!box) {
                        return false;
                    }
                    a.pause();
                    a.currentTime = pseudo.includes("-new(") ? 0 : a.effect.getComputedTiming().endTime - 1;
                    const shift = new DOMMatrix(getComputedStyle(root, pseudo).transform).m41;
                    const r = box.getBoundingClientRect();
                    a.finish();
                    return r.left + shift >= innerWidth - 1 || r.right + shift <= 1;
                })
                .map((a) => a.effect.pseudoElement);
        }), { once: true })
        """;

    [Theory]
    [InlineData(E2EFixture.Chromium, "/today", ".tabbar .tab[href='/food']", "old(page) old(large-title) new(page) new(large-title)")]
    [InlineData(E2EFixture.WebKit, "/today", ".tabbar .tab[href='/food']", "old(page) old(large-title) new(page) new(large-title)")]
    [InlineData(E2EFixture.Chromium, "/food", ".tabbar .tab[href='/today']", "old(page) old(large-title) new(page) new(large-title)")]
    [InlineData(E2EFixture.WebKit, "/food", ".tabbar .tab[href='/today']", "old(page) old(large-title) new(page) new(large-title)")]
    [InlineData(E2EFixture.Chromium, "/settings", "#main a[href='/settings/general']", "new(page) new(large-title)")]
    [InlineData(E2EFixture.WebKit, "/settings", "#main a[href='/settings/general']", "new(page) new(large-title)")]
    [InlineData(E2EFixture.Chromium, "/settings/general", ".topbar-back", "old(page)")]
    [InlineData(E2EFixture.WebKit, "/settings/general", ".topbar-back", "old(page)")]
    public async Task A_sliding_page_starts_and_ends_off_screen(string browser, string from, string tap, string slides)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, width: 1280);
        await phone.GoAsync(from);
        await phone.Page.EvaluateAsync(WatchOffScreen);

        await phone.NavigateByAsync(() => phone.TapAsync(tap));

        var offscreen = await phone.Page.EvaluateAsync<string[]>("window.__offscreen");
        offscreen.Should().Contain(slides.Split(' ').Select((s) => "::view-transition-" + s), "the page next to this one is not shown before it slides in");
        phone.ShouldHaveNoErrors();
    }
}
