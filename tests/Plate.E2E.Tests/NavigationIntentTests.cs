using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Plate.E2E.Tests;

public class NavigationIntentTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    private static async Task NavigateNewPageAsync(Phone phone, Func<Task> action)
    {
        // Another request is deliberately held, so wait for this swap rather than for all loading indicators.
        var before = await phone.Page.EvaluateAsync<int>("() => window.__gymPages");
        await action();
        await phone.Page.WaitForFunctionAsync("before => window.__gymPages > before && !document.documentElement.dataset.nav", before,
            new() { Timeout = 10_000 });
    }

    private static Task HoldTrainAsync(Phone phone, string mode = "response") => phone.Page.EvaluateAsync("""
        mode => {
            const original = window.fetch;
            const append = document.head.appendChild;
            let held = false;
            window.staleFallbacks = 0;
            window.probeScripts = 0;
            document.head.appendChild = function (node) {
                if (node.src?.includes("/js/p4-probe-")) {
                    window.probeScripts++;
                    if (window.probeScripts === 1) {
                        window.trainHeld = true;
                        window.releaseTrain = () => node.dispatchEvent(new Event("load"));
                    } else setTimeout(() => node.dispatchEvent(new Event("load")), 0);
                    return node;
                }
                return append.call(this, node);
            };
            window.fetch = async (url, options) => {
                const response = await original(url, options);
                if (new URL(url, location.href).pathname !== "/train" || held) return response;
                held = true;
                if (mode !== "script") {
                    window.trainHeld = true;
                    await new Promise(resolve => { window.releaseTrain = resolve; });
                    if (mode === "failure") throw new TypeError("Synthetic late network failure");
                }
                if (mode === "version" || mode === "script") return {
                    url: response.url, status: response.status, headers: response.headers,
                    text: async () => {
                        const html = await response.text();
                        return mode === "version" ? html.replace(/data-version="[^"]+"/, 'data-version="another-build"')
                            : html.replace("</main>", '<script src="/js/p4-probe-one.js"></script><script src="/js/p4-probe-two.js"></script></main>');
                    }
                };
                return response;
            };
            window.oldNavDone = window.gymNav("/train", { fallback: () => { window.staleFallbacks++; } });
        }
        """, mode);

    private static async Task ReleaseTrainAsync(Phone phone)
    {
        await phone.Page.EvaluateAsync("() => window.releaseTrain()");
        await phone.Page.EvaluateAsync("() => window.oldNavDone");
        // A stale full-load fallback crosses the browser navigation boundary after go() returns.
        await phone.Page.WaitForTimeoutAsync(150);
        await phone.SettledAsync();
    }

    private static async Task ShouldShowFoodAsync(Phone phone)
    {
        new Uri(phone.Page.Url).AbsolutePath.Should().Be("/food");
        (await phone.Page.Locator("html").GetAttributeAsync("data-tab")).Should().Be("food");
        (await phone.Page.Locator(".tabbar .is-active").GetAttributeAsync("href")).Should().Be("/food");
    }

    [Theory]
    [InlineData(E2EFixture.Chromium, "response")]
    [InlineData(E2EFixture.WebKit, "response")]
    [InlineData(E2EFixture.Chromium, "failure")]
    [InlineData(E2EFixture.WebKit, "failure")]
    [InlineData(E2EFixture.Chromium, "version")]
    [InlineData(E2EFixture.WebKit, "version")]
    [InlineData(E2EFixture.Chromium, "script")]
    [InlineData(E2EFixture.WebKit, "script")]
    public async Task An_obsolete_response_failure_or_page_script_cannot_replace_the_latest_choice(string browser, string mode)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/today");
        await HoldTrainAsync(phone, mode);
        await phone.Page.WaitForFunctionAsync("() => window.trainHeld");
        await NavigateNewPageAsync(phone, () => phone.Page.Locator(".tabbar a[href='/food']").TapAsync());
        await ReleaseTrainAsync(phone);
        await ShouldShowFoodAsync(phone);
        (await phone.Page.EvaluateAsync<int>("() => window.staleFallbacks")).Should().Be(0);
        if (mode == "script") (await phone.Page.EvaluateAsync<int>("() => window.probeScripts")).Should().Be(1);
        if (mode == "failure")
        {
            // The current intent must still use its ordinary offline fallback.
            await phone.Page.EvaluateAsync("""
                async () => {
                    window.fetch = () => Promise.reject(new TypeError("Synthetic current failure"));
                    await window.gymNav("/train", { fallback: () => { window.staleFallbacks++; } });
                }
                """);
            (await phone.Page.EvaluateAsync<int>("() => window.staleFallbacks")).Should().Be(1);
        }
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Back_supersedes_an_inflight_navigation(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/today");
        await NavigateNewPageAsync(phone, () => phone.Page.Locator(".tabbar a[href='/food']").TapAsync());
        await HoldTrainAsync(phone);
        await phone.Page.WaitForFunctionAsync("() => window.trainHeld");
        await NavigateNewPageAsync(phone, async () => { await phone.Page.GoBackAsync(); });
        await ReleaseTrainAsync(phone);
        new Uri(phone.Page.Url).AbsolutePath.Should().Be("/today");
        (await phone.Page.Locator("html").GetAttributeAsync("data-tab")).Should().Be("today");
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_late_post_still_saves_but_does_not_replace_a_newer_get(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/food");
        await phone.Page.Locator("#meal-dinner [data-sheet-open='quick-sheet']").TapAsync();
        await phone.Page.Locator("#quick-body input[name=kcal]").FillAsync("650");
        await phone.Page.EvaluateAsync("""
            () => {
                const original = window.fetch;
                window.fetch = async (url, options) => {
                    if (new URL(url, location.href).pathname === "/food/quick" && options?.method === "POST") {
                        window.postHeld = true;
                        await new Promise(resolve => { window.releasePost = resolve; });
                    }
                    return original(url, options);
                };
                const form = document.querySelector("#quick-body form");
                window.postNavDone = window.gymNav(form.action, { init: { method: "POST", body: new URLSearchParams(new FormData(form)) } });
            }
            """);
        await phone.Page.WaitForFunctionAsync("() => window.postHeld");
        await phone.Page.EvaluateAsync("() => window.gymSheet.hide()");
        await NavigateNewPageAsync(phone, () => phone.Page.Locator(".tabbar a[href='/train']").TapAsync());
        await phone.Page.EvaluateAsync("() => window.releasePost()");
        await phone.Page.EvaluateAsync("() => window.postNavDone");
        await phone.SettledAsync();
        new Uri(phone.Page.Url).AbsolutePath.Should().Be("/train");
        await using var db = app.Db.CreateContext(null);
        var userId = await db.Users.Where(u => u.Email == phone.Email).Select(u => u.Id).SingleAsync();
        (await db.FoodLog.IgnoreQueryFilters().CountAsync(x => x.UserId == userId)).Should().Be(1);
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task An_obsolete_completion_cannot_clear_the_current_loading_indicator(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/today");
        await HoldTrainAsync(phone);
        await phone.Page.WaitForFunctionAsync("() => window.trainHeld");
        await phone.Page.EvaluateAsync("""
            () => {
                const original = window.fetch;
                window.fetch = async (url, options) => {
                    const response = await original(url, options);
                    if (new URL(url, location.href).pathname === "/food") {
                        window.foodHeld = true;
                        await new Promise(resolve => { window.releaseFood = resolve; });
                    }
                    return response;
                };
                window.foodNavDone = window.gymNav("/food");
            }
            """);
        await phone.Page.WaitForFunctionAsync("() => window.foodHeld && document.documentElement.classList.contains('is-navigating')");
        await phone.Page.EvaluateAsync("() => window.releaseTrain()");
        await phone.Page.EvaluateAsync("() => window.oldNavDone");
        (await phone.Page.Locator("html").EvaluateAsync<bool>("el => el.classList.contains('is-navigating')")).Should().BeTrue();
        await phone.Page.EvaluateAsync("() => window.releaseFood()");
        await phone.Page.EvaluateAsync("() => window.foodNavDone");
        await phone.SettledAsync();
        await ShouldShowFoodAsync(phone);
        (await phone.Page.Locator("html").EvaluateAsync<bool>("el => el.classList.contains('is-navigating')")).Should().BeFalse();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_current_build_version_change_still_loads_the_page_normally(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/today");
        var origin = await phone.Page.EvaluateAsync<double>("() => performance.timeOrigin");
        await HoldTrainAsync(phone, "version");
        await phone.Page.WaitForFunctionAsync("() => window.trainHeld");
        await phone.Page.EvaluateAsync("() => window.releaseTrain()");
        await phone.Page.WaitForURLAsync("**/train");
        await phone.SettledAsync();
        (await phone.Page.EvaluateAsync<double>("() => performance.timeOrigin")).Should().NotBe(origin);
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_pending_set_replays_once_while_a_newer_page_wins(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Intent test", "Bench press");
        var session = await phone.StartSessionAsync(template);
        await phone.Page.EvaluateAsync("""
            () => {
                const original = window.fetch;
                window.allowSetReplay = false;
                window.fetch = (url, options) => new URL(url, location.href).pathname === "/train/sets" && !window.allowSetReplay
                    ? Promise.reject(new TypeError("Synthetic unavailable set connection")) : original(url, options);
            }
            """);
        await phone.LogSetAsync(phone.Page.Locator(".rows.working .set-row").First, "60", "10");
        await phone.Page.Locator("#queue-status").WaitForAsync();
        var clientId = await phone.Page.EvaluateAsync<string>("() => window.gymQueue('gym.pendingSets').load()[0].clientId");
        await HoldTrainAsync(phone);
        await phone.Page.WaitForFunctionAsync("() => window.trainHeld");
        await NavigateNewPageAsync(phone, () => phone.Page.Locator(".tabbar a[href='/food']").TapAsync());
        await phone.Page.EvaluateAsync("() => { window.allowSetReplay = true; window.dispatchEvent(new Event('online')); }");
        await phone.Page.WaitForFunctionAsync("() => window.gymQueue('gym.pendingSets').load().length === 0");
        await ReleaseTrainAsync(phone);
        await ShouldShowFoodAsync(phone);
        await using var db = app.Db.CreateContext(null);
        (await db.Sets.IgnoreQueryFilters().CountAsync(x => x.ClientId == Guid.Parse(clientId) && x.SessionExercise.SessionId == session)).Should().Be(1);
    }
}
