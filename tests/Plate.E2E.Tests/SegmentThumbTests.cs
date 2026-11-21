using FluentAssertions;

namespace Plate.E2E.Tests;

/// <summary>
/// A segmented control's selected segment (the thumb) glides to another segment when its page changes in place, and
/// otherwise travels with its page. Named on a push it had no partner on the page that was left, so it faded in at its
/// final spot over the page while the page was still sliding in.
/// </summary>
public class SegmentThumbTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    // The kind of page change, the thumb's transition name during it, and what the browser animates on its own.
    private const string Watch = """
        () => document.addEventListener("gym:page", (e) => {
            const thumb = document.querySelector(".segmented[data-thumb] .segment:is(.is-active, :has(:checked))");
            window.__change = { nav: e.detail.nav, name: getComputedStyle(thumb).viewTransitionName, names: [] };
            requestAnimationFrame(() => {
                window.__change.names = document.getAnimations()
                    .map((a) => a.effect && a.effect.pseudoElement || "")
                    .filter((p) => p.startsWith("::view-transition-group"));
            });
        }, { once: true })
        """;

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task The_theme_control_slides_in_with_its_page(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/settings");
        await phone.Page.EvaluateAsync(Watch);

        await phone.NavigateByAsync(() => phone.TapAsync("#main a[href='/settings/general']"));

        (await phone.Page.EvaluateAsync<string>("window.__change.nav")).Should().Be("forward");
        (await phone.Page.EvaluateAsync<string>("window.__change.name")).Should().Be("none", "a thumb with its own transition shows up at its final spot before its page has slid in");
        (await phone.Page.EvaluateAsync<string[]>("window.__change.names")).Should().NotContain("::view-transition-group(segment-thumb)");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task The_theme_control_leaves_with_its_page(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/settings");
        await phone.NavigateByAsync(() => phone.TapAsync("#main a[href='/settings/general']"));
        // Read as the page that is left is captured, just before the new one goes in.
        await phone.Page.EvaluateAsync("""
            () => {
                const thumb = document.querySelector(".segmented[data-thumb] .segment:has(:checked)");
                document.addEventListener("gym:beforeswap", () => {
                    window.__left = document.documentElement.dataset.nav + " " + getComputedStyle(thumb).viewTransitionName;
                }, { once: true });
            }
            """);

        await phone.NavigateByAsync(() => phone.TapAsync(".topbar-back"));

        (await phone.Page.EvaluateAsync<string>("window.__left")).Should().Be("back none", "the page that is left takes its thumb with it");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Another_chart_range_moves_the_thumb_in_place(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/progress");
        await phone.Page.EvaluateAsync(Watch);

        await phone.NavigateByAsync(() => phone.TapAsync("#main .segmented[data-thumb] a.segment:not(.is-active)"));

        (await phone.Page.EvaluateAsync<string>("window.__change.nav")).Should().Be("swap");
        (await phone.Page.EvaluateAsync<string>("window.__change.name")).Should().Be("segment-thumb");
        (await phone.Page.EvaluateAsync<string[]>("window.__change.names")).Should().Contain("::view-transition-group(segment-thumb)", "the thumb glides to the new range");
        phone.ShouldHaveNoErrors();
    }
}
