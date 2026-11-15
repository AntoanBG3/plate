using FluentAssertions;

namespace Plate.E2E.Tests;

/// <summary>
/// The 2026-11-14 motion pass: one entrance per page change, changes on a page crossfade in place, and nothing that
/// is over (a closing sheet, a set already on its way) gets in the way of the next tap.
/// </summary>
public class MotionRefineTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    // The kind of page change nav.js chose, and whether any block of the new page was given an entrance of its own.
    private const string WatchPageChange = """
        () => document.addEventListener("gym:page", (e) => {
            const main = document.getElementById("main");
            window.__change = {
                nav: e.detail.nav,
                moving: [...main.children].filter((c) => c.getAnimations().length > 0).length,
                entering: main.classList.contains("is-entering"),
            };
        }, { once: true })
        """;

    private sealed record PageChange(string Nav, int Moving, bool Entering);

    private static async Task<PageChange> PageChangeAsync(Phone phone) => new(
        await phone.Page.EvaluateAsync<string>("window.__change.nav"),
        await phone.Page.EvaluateAsync<int>("window.__change.moving"),
        await phone.Page.EvaluateAsync<bool>("window.__change.entering"));

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Another_day_on_the_same_page_crossfades_in_place(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/food");
        await phone.Page.EvaluateAsync(WatchPageChange);

        await phone.NavigateByAsync(() => phone.Page.Locator("#main a[href^='/food?date=']").First.TapAsync());

        var change = await PageChangeAsync(phone);
        change.Nav.Should().Be("swap", "the same page with other contents is not a new place");
        change.Moving.Should().Be(0, "its blocks don't rise in again");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_pushed_page_is_its_own_entrance(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/train");
        // Seen before, so the first-sight animations (counts, blocks below the fold) are out of the picture.
        await phone.GoAsync("/train/history");
        await phone.GoAsync("/train");
        await phone.Page.EvaluateAsync(WatchPageChange);

        await phone.NavigateByAsync(() => phone.TapAsync("#main a[href='/train/history']"));

        var change = await PageChangeAsync(phone);
        change.Nav.Should().Be("forward");
        change.Entering.Should().BeFalse();
        change.Moving.Should().Be(0, "the page slides in as a whole; nothing inside it moves as well");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_closing_sheet_lets_taps_through(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/food");
        await phone.TapAsync("#main [data-sheet-open='quick-sheet']");
        await phone.Page.WaitForFunctionAsync("() => document.body.classList.contains('sheet-open')");
        (await phone.Page.EvaluateAsync<string>("() => getComputedStyle(document.getElementById('quick-sheet')).pointerEvents")).Should().Be("auto");

        // Still in the page while it slides away, but no longer in the way.
        var closing = await phone.Page.EvaluateAsync<string[]>(
            "() => { window.gymSheet.hide(); const s = document.getElementById('quick-sheet'); return [String(s.hidden), getComputedStyle(s).pointerEvents]; }");
        closing.Should().Equal("false", "none");

        await phone.Page.WaitForFunctionAsync("() => document.getElementById('quick-sheet').hidden");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_set_sent_straight_away_never_raises_the_pending_banner(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press");
        await phone.StartSessionAsync(template);
        await phone.Page.EvaluateAsync("""
            () => {
                const status = document.getElementById("queue-status");
                window.__bannerShown = !status.hidden;
                new MutationObserver(() => { window.__bannerShown ||= !status.hidden; }).observe(status, { attributes: true });
            }
            """);

        await phone.LogSetAsync(phone.Page.Locator(".rows.working .set-row").First, "60", "10");
        await phone.Page.Locator(".rows.working .set-row.done").First.WaitForAsync();

        (await phone.Page.EvaluateAsync<bool>("window.__bannerShown")).Should().BeFalse("the banner is for sets that are waiting, and it moves the page when it shows");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Bars_fill_on_a_pages_first_sight_only(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.WithTargetsAsync(app);
        // Counted from before the page's own scripts run, so a fill that starts with the first paint is seen.
        await phone.Page.AddInitScriptAsync("""
            window.__fills = 0;
            document.addEventListener("animationstart", (e) => { if (e.animationName === "meter-in") { window.__fills++; } }, true);
            """);
        // The page gives its bars one second to start filling (motion.js) and then shows them full. A slow machine
        // can take longer than that to paint the first frame, and then nothing fills: with the page's timers held,
        // the test doesn't depend on how fast the browser is.
        var now = DateTime.UtcNow;
        await phone.Page.Clock.InstallAsync(new() { TimeDate = now.AddMinutes(-1) });
        await phone.Page.Clock.PauseAtAsync(now);
        await phone.GoAsync("/food");
        (await phone.Page.Locator(".meter i").CountAsync()).Should().BeGreaterThan(0, "the test needs the day's bars");
        await phone.Page.WaitForFunctionAsync("() => window.__fills > 0");
        (await phone.Page.EvaluateAsync<bool>("() => window.__fills === document.querySelectorAll('.meter i').length"))
            .Should().BeTrue("each bar fills once, from the first paint, not full and then again");

        await phone.GoAsync("/food");
        await phone.Page.WaitForTimeoutAsync(300);
        (await phone.Page.EvaluateAsync<int>("window.__fills")).Should().Be(0, "on a return the bars are just there");
        phone.ShouldHaveNoErrors();
    }
}
