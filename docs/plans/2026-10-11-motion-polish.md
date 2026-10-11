# Motion Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Four refinements taken from apple.com: closed menus leave the accessibility tree, first-sight animations wait until on screen, row animations scale with height, and scroll areas fade at the edges that have more content.

**Architecture:** Front end only. One shared helper, `window.gymWhenSeen(el, fn)` in `pages.js` (loaded first, without `defer`, so both `motion.js` and the page script `progress.js` can use it), runs a callback once an element is on screen. The rest is CSS in `app.css` and small changes to `motion.js` and `progress.js`. Server, database and strings are unchanged.

**Tech Stack:** Vanilla JS, CSS, Chart.js 4.5.1 (already in `wwwroot/lib/chartjs`), Playwright browser tests in xUnit + FluentAssertions (Chromium and WebKit).

**Spec:** `docs/plans/2026-10-11-motion-polish-design.md`

## Global Constraints

- No new NuGet packages or front-end libraries; no CDNs (AGENTS.md).
- Mobile first: check layouts at 390 px; touch targets stay at least 44 px.
- `prefers-reduced-motion: reduce`: nothing is hidden and nothing moves beyond the existing 150 ms fades.
- Anything hidden for an animation is hidden by a class that JavaScript adds, so the page shows if a script fails.
- Values from the spec: menu items stagger `18ms` apart after a `40ms` lead, `200ms`, from `translateY(-4px)`; seen trigger `rootMargin: "0px 0px -15% 0px"` (top within 85 % of the viewport height); rise `translateY(16px)`, `420ms`, `--ease-gentle`, `60ms` between blocks entering together; appear `clamp(240, 200 + height / 2, 480)` ms; collapse 75 % of that, never longer than the element's `hx-swap` `swap:<n>ms`; fades `16px`.
- No new user-facing strings (no `strings.bg.json` changes).
- Commit messages: plain, descriptive.
- Test cadence: after each task run its new tests, the listed affected browser classes, and all Core and Web tests. The full browser suite runs once, in Task 7.

## Review Focus

1. A keyboard user presses Escape inside an open menu: focus goes back to the menu's button, instead of being lost when the menu becomes `visibility: hidden`. Test in Task 1.
2. The theme changes on Progress before a lower chart has been seen: the old chart's pending entrance must not run on the destroyed chart, and the redrawn chart doesn't play an entrance. Test in Task 4.
3. A soft navigation happens while blocks are still waiting to rise in: no console errors, and no block on the new page is left invisible on screen. Test in Task 3.
4. A screen taller than the page (iPad, desktop): nothing is below the fold, so nothing is hidden. Test in Task 3.
5. A scroll area with nothing to scroll (one search result): no fade at either edge. Test in Task 6.

## Commands

- Build: `dotnet build`
- One browser test class: `dotnet test tests/GymTracker.E2E.Tests --filter "FullyQualifiedName~MotionPolishTests"`
- One browser test: `dotnet test tests/GymTracker.E2E.Tests --filter "FullyQualifiedName~MotionPolishTests.<MethodName>"`
- Core and Web: `dotnet test tests/GymTracker.Core.Tests && dotnet test tests/GymTracker.Web.Tests`
- Everything: `dotnet test`

Every browser test is a `[Theory]` with `[InlineData(E2EFixture.Chromium)]` and `[InlineData(E2EFixture.WebKit)]`, like the existing classes.

---

### Task 1: Closed menus leave the accessibility tree

**Files:**
- Create: `tests/GymTracker.E2E.Tests/MotionPolishTests.cs`
- Modify: `src/GymTracker.Web/wwwroot/css/app.css` (the `.menu` rules in "Context menu" around line 689, and the reduced-motion block around line 875)
- Modify: `src/GymTracker.Web/wwwroot/js/motion.js` (`closeMenus`, around line 326)

**Interfaces:**
- Consumes: `E2EFixture.SignedInPhoneAsync`, `Workouts.CreateTemplateAsync`, `Phone.GoAsync`.
- Produces: the `MotionPolishTests` class that Tasks 2 to 6 add methods to.

- [ ] **Step 1: Write the failing test**

Create `tests/GymTracker.E2E.Tests/MotionPolishTests.cs`:

```csharp
using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace GymTracker.E2E.Tests;

/// <summary>Motion refinements taken from apple.com (docs/plans/2026-10-11-motion-polish-design.md).</summary>
public class MotionPolishTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    private const string MenuVisibility = "m => getComputedStyle(m).visibility";

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_closed_menu_cannot_be_focused_and_Escape_returns_to_its_button(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press", "Dip");
        await phone.GoAsync($"/train/templates/{template}");
        var menu = phone.Page.Locator(".topbar-trailing .menu");
        var trigger = phone.Page.Locator(".topbar-trailing [data-menu-trigger]");

        (await menu.EvaluateAsync<string>(MenuVisibility)).Should().Be("hidden");
        (await menu.EvaluateAsync<bool>("m => { const item = m.querySelector('[role=menuitem]'); item.focus(); return document.activeElement === item; }"))
            .Should().BeFalse("a closed menu's items must not take focus or be read out");

        // Opened from the keyboard, the menu focuses its first item (motion.js, click with detail 0).
        await trigger.FocusAsync();
        await phone.Page.Keyboard.PressAsync("Enter");
        (await menu.EvaluateAsync<string>(MenuVisibility)).Should().Be("visible");
        (await menu.EvaluateAsync<bool>("m => m.contains(document.activeElement)")).Should().BeTrue();

        await phone.Page.Keyboard.PressAsync("Escape");
        await phone.Page.WaitForFunctionAsync("() => getComputedStyle(document.querySelector('.topbar-trailing .menu')).visibility === 'hidden'");
        (await trigger.EvaluateAsync<bool>("t => document.activeElement === t")).Should().BeTrue("closing returns focus to the menu's button");
        phone.ShouldHaveNoErrors();
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/GymTracker.E2E.Tests --filter "FullyQualifiedName~MotionPolishTests.A_closed_menu"`
Expected: FAIL on both browsers, `Expected ... to be "hidden", but "visible" ...`.

- [ ] **Step 3: Hide the closed menu with visibility, and stagger its items in**

In `app.css`, replace:

```css
    transform-origin: top right; transform: scale(0.6); opacity: 0; pointer-events: none;
    transition: transform 300ms var(--ease-snappy), opacity 160ms var(--ease-out);
}
.menu.is-open { transform: none; opacity: 1; pointer-events: auto; }
```

with:

```css
    transform-origin: top right; transform: scale(0.6); opacity: 0; pointer-events: none;
    /* Hidden once the close animation ends, so a closed menu can't be tabbed to or read out (apple.com's nav does the
       same with visibility step-end); visible from the first frame when it opens. */
    visibility: hidden;
    transition: transform 300ms var(--ease-snappy), opacity 160ms var(--ease-out), visibility 0s linear 300ms;
}
.menu.is-open {
    transform: none; opacity: 1; pointer-events: auto; visibility: visible;
    transition: transform 300ms var(--ease-snappy), opacity 160ms var(--ease-out), visibility 0s;
}
/* Items follow the panel in, a little apart; closing has no stagger. The exercise card menu has seven items, so the seventh and later share `--i: 6`. */
.menu > * { opacity: 0; transform: translateY(-4px); transition: opacity 120ms var(--ease-out), transform 120ms var(--ease-out); }
.menu.is-open > * {
    opacity: 1; transform: none;
    transition: opacity 200ms var(--ease-out) calc(40ms + var(--i, 0) * 18ms), transform 200ms var(--ease-out) calc(40ms + var(--i, 0) * 18ms);
}
.menu > :nth-child(2) { --i: 1; }
.menu > :nth-child(3) { --i: 2; }
.menu > :nth-child(4) { --i: 3; }
.menu > :nth-child(5) { --i: 4; }
.menu > :nth-child(6) { --i: 5; }
.menu > :nth-child(n+7) { --i: 6; }
```

In the `@media (prefers-reduced-motion: reduce)` block of `app.css`, replace:

```css
    .sheet-scrim, .menu, .toast, .topbar, .topbar-title { transition: opacity 150ms linear !important; }
    .menu, .topbar-title { transform: none !important; }
```

with:

```css
    .sheet-scrim, .toast, .topbar, .topbar-title { transition: opacity 150ms linear !important; }
    .menu { transition: opacity 150ms linear, visibility 0s linear 150ms !important; }
    .menu.is-open { transition: opacity 150ms linear, visibility 0s !important; }
    .menu, .menu > *, .topbar-title { transform: none !important; }
    .menu > * { transition-delay: 0s !important; }
```

- [ ] **Step 4: Return focus to the button when a menu closes around it**

In `motion.js`, replace `closeMenus`:

```js
    function closeMenus(except) {
        $$(".menu.is-open").forEach((menu) => {
            if (menu !== except) {
                menu.classList.remove("is-open");
                const trigger = menu.parentElement.querySelector("[data-menu-trigger]");
                if (trigger) {
                    trigger.setAttribute("aria-expanded", "false");
                }
            }
        });
    }
```

with:

```js
    function closeMenus(except) {
        $$(".menu.is-open").forEach((menu) => {
            if (menu !== except) {
                menu.classList.remove("is-open");
                const trigger = menu.parentElement.querySelector("[data-menu-trigger]");
                if (trigger) {
                    trigger.setAttribute("aria-expanded", "false");
                    // A closed menu can't hold focus (it becomes visibility: hidden), so focus goes back to its button.
                    if (menu.contains(document.activeElement)) {
                        trigger.focus({ preventScroll: true });
                    }
                }
            }
        });
    }
```

This also means a menu item that opens a sheet (Notes & bar, Save as a meal) now gives the sheet the button as the place to return focus to, instead of an item in a hidden menu.

- [ ] **Step 5: Run the test and the affected classes**

Run: `dotnet test tests/GymTracker.E2E.Tests --filter "FullyQualifiedName~MotionPolishTests|FullyQualifiedName~NavigationTests|FullyQualifiedName~ReorderTests|FullyQualifiedName~ConfirmSheetTests|FullyQualifiedName~UntestedAreasTests|FullyQualifiedName~UndoToastTests"`
Expected: PASS. Then run Core and Web: PASS.

- [ ] **Step 6: Commit**

```bash
git add tests/GymTracker.E2E.Tests/MotionPolishTests.cs src/GymTracker.Web/wwwroot/css/app.css src/GymTracker.Web/wwwroot/js/motion.js
git commit -m "Take closed menus out of the tab order and stagger their items in

A closed menu was only transparent, so Tab and VoiceOver still reached
its items. It is now visibility: hidden once the close animation ends,
as apple.com's nav does. Escape returns focus to the menu's button."
```

---

### Task 2: First-sight animations start when on screen

**Files:**
- Modify: `src/GymTracker.Web/wwwroot/js/pages.js` (add `gymWhenSeen`)
- Modify: `src/GymTracker.Web/wwwroot/js/motion.js` (`reveal`, its first call, the `gym:page` handler, the header comment)
- Test: `tests/GymTracker.E2E.Tests/MotionPolishTests.cs`

**Interfaces:**
- Produces: `window.gymWhenSeen(el: Element, fn: (el: Element, index: number) => void): void`. It calls `fn` at once (index 0) if any part of `el` is on screen, if `el` has no box (`display: none`), or if `IntersectionObserver` is missing. Otherwise it calls `fn` once, when `el`'s top comes within 85 % of the viewport height, with `index` = its position among the elements entering in the same observer callback. Pending waits are dropped on `gym:beforeswap`.
- Produces: in motion.js, `firstSight(): boolean` and `reveal(scope: ParentNode, animate: boolean)`. Task 3 uses both.
- Produces: test helpers `FirstSightAsync(Phone, string path)` and `SeedWeightsAsync(Phone)` in `MotionPolishTests`. Tasks 3 and 4 use them.

- [ ] **Step 1: Write the failing test, the reduced-motion guard and the helpers**

Add to `MotionPolishTests`:

```csharp
    // A short screen, so the Body section (trend value and sparkline) on Today starts below the fold.
    private const int ShortScreen = 480;

    /// <summary>Opens <paramref name="path"/> as if for the first time this session, so first-sight animations play.</summary>
    private static async Task FirstSightAsync(Phone phone, string path)
    {
        await phone.Page.EvaluateAsync("() => sessionStorage.clear()");
        await phone.GoAsync(path);
    }

    /// <summary>Five weigh-ins up to today, enough for a trend and a sparkline on Today.</summary>
    private async Task SeedWeightsAsync(Phone phone)
    {
        await using var db = app.Db.CreateContext(null);
        var id = await db.Users.Where(u => u.Email == phone.Email).Select(u => u.Id).SingleAsync();
        var today = UserClock.Today(DateTimeOffset.UtcNow, UserProfile.DefaultTimeZoneId);
        for (var i = 0; i < 5; i++)
        {
            db.Bodyweights.Add(new BodyweightEntry { UserId = id, Date = today.AddDays(-i), WeightKg = 80m + i * 0.3m });
        }

        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Below_the_fold_animations_wait_until_scrolled_into_view(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await SeedWeightsAsync(phone);
        await phone.Page.SetViewportSizeAsync(390, ShortScreen);
        await FirstSightAsync(phone, "/today");

        var spark = phone.Page.Locator(".spark-line");
        (await spark.EvaluateAsync<bool>("e => e.getBoundingClientRect().top > innerHeight")).Should().BeTrue("the test needs the sparkline below the fold");
        (await spark.EvaluateAsync<int>("e => e.getAnimations().length")).Should().Be(0, "the line draws in when it is seen, not at load");

        await spark.ScrollIntoViewIfNeededAsync();
        await phone.Page.WaitForFunctionAsync("() => document.querySelector('.spark-line').getAnimations().length > 0");
        await phone.Page.WaitForFunctionAsync("() => { const v = document.querySelector('#main section:has(.spark) [data-count]'); return v.textContent.trim() === v.dataset.count; }");
        phone.ShouldHaveNoErrors();
    }

    // A guard: it passes before and after this work, and keeps reduced motion from ever hiding content.
    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Reduced_motion_hides_nothing(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await SeedWeightsAsync(phone);
        await phone.Page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        await phone.Page.SetViewportSizeAsync(390, ShortScreen);

        foreach (var path in new[] { "/today", "/progress" })
        {
            await FirstSightAsync(phone, path);
            (await phone.Page.Locator(".is-pending-rise, .is-rising").CountAsync()).Should().Be(0, $"nothing on {path} waits to rise in");
            (await phone.Page.EvaluateAsync<bool>("() => [...document.querySelectorAll('[data-count]')].every(e => e.textContent.trim() === e.dataset.count)"))
                .Should().BeTrue($"every number on {path} shows its value at once");
        }

        phone.ShouldHaveNoErrors();
    }
```

- [ ] **Step 2: Run them to verify the first fails and the guard passes**

Run: `dotnet test tests/GymTracker.E2E.Tests --filter "FullyQualifiedName~MotionPolishTests.Below_the_fold_animations|FullyQualifiedName~MotionPolishTests.Reduced_motion"`
Expected: `Below_the_fold_animations_wait_until_scrolled_into_view` FAILS on `Expected ... to be 0` (the line is already animating at load). `Reduced_motion_hides_nothing` PASSES. If the first test fails on the "below the fold" precondition instead, lower `ShortScreen` until the Body section starts below the fold, and note it in the commit.

- [ ] **Step 3: Add `gymWhenSeen` to pages.js**

In `pages.js`, before the closing `})();`, add:

```js

    // Runs fn(el, index) once el is on screen: at once if any of it shows already (or it has no box to wait for),
    // otherwise when its top comes within 85 % of the viewport height, as apple.com's staggered reveals do. index is
    // the element's place among those coming into view together, for staggering. Waits end when the page is swapped.
    let seenObserver = null;
    const waiting = new Map();
    window.gymWhenSeen = (el, fn) => {
        const box = el.getBoundingClientRect();
        if (!("IntersectionObserver" in window) || (box.width === 0 && box.height === 0) || (box.top < window.innerHeight && box.bottom > 0)) {
            fn(el, 0);
            return;
        }
        if (!seenObserver) {
            seenObserver = new IntersectionObserver((entries) => {
                let index = 0;
                entries.forEach((entry) => {
                    if (!entry.isIntersecting) {
                        return;
                    }
                    seenObserver.unobserve(entry.target);
                    const callbacks = waiting.get(entry.target) || [];
                    waiting.delete(entry.target);
                    callbacks.forEach((callback) => callback(entry.target, index));
                    index++;
                });
            }, { rootMargin: "0px 0px -15% 0px" });
        }
        if (!waiting.has(el)) {
            waiting.set(el, []);
            seenObserver.observe(el);
        }
        waiting.get(el).push(fn);
    };
    document.addEventListener("gym:beforeswap", () => {
        if (seenObserver) {
            seenObserver.disconnect();
        }
        waiting.clear();
    });
```

- [ ] **Step 4: Make `reveal` wait for each element to be seen**

In `motion.js`, replace the whole `reveal` function and the line after it:

```js
    function reveal(scope) {
        const key = "reveal:" + location.pathname;
        const animate = !reduce.matches && !seenBefore(key);
        ...
    }
    reveal(document);
```

(that is, from `function reveal(scope) {` through `reveal(document);`) with:

```js
    // First sight of a page this session, with motion allowed.
    function firstSight() {
        return !reduce.matches && !seenBefore("reveal:" + location.pathname);
    }

    // Each animation starts once its element is on screen (pages.js gymWhenSeen). Until then the element shows where
    // its animation starts (a count at data-from, a ring or line undrawn), so it never jumps back from its final value.
    function reveal(scope, animate) {
        $$("[data-count]", scope).forEach((el) => {
            if (!animate) {
                return;
            }
            el.dataset.from = el.dataset.from || "0";
            countTo(el, Number(el.dataset.from), 0);
            window.gymWhenSeen(el, () => countTo(el, Number(el.dataset.count), 900));
        });
        $$("[data-progress]", scope).forEach((el) => {
            const value = Math.max(0, Math.min(1, Number(el.dataset.progress)));
            const len = el.getTotalLength();
            const end = len * (1 - value);
            el.style.strokeDasharray = `${len} ${len}`;
            if (value === 0) {
                el.style.opacity = "0";
            }
            if (!animate || value === 0) {
                el.style.strokeDashoffset = `${end}`;
                return;
            }
            el.style.strokeDashoffset = `${len}`;
            window.gymWhenSeen(el, () => {
                el.style.strokeDashoffset = `${end}`;
                el.animate([{ strokeDashoffset: len }, { strokeDashoffset: end }],
                    { duration: 1100, delay: 120, easing: "cubic-bezier(.2,.8,.2,1)", fill: "backwards" });
            });
        });
        $$("[data-draw]", scope).forEach((el) => {
            if (!animate) {
                return;
            }
            const len = el.getTotalLength();
            el.style.strokeDasharray = `${len} ${len}`;
            el.style.strokeDashoffset = `${len}`;
            window.gymWhenSeen(el, () => {
                el.style.strokeDashoffset = "0";
                el.animate([{ strokeDashoffset: len }, { strokeDashoffset: 0 }],
                    { duration: 900, delay: 250, easing: "cubic-bezier(.3,.7,.2,1)", fill: "backwards" });
            });
        });
    }
    reveal(document, firstSight());
```

In the `gym:page` handler, replace:

```js
        const main = document.getElementById("main");
        reveal(main);
```

with:

```js
        const main = document.getElementById("main");
        reveal(main, firstSight());
```

In the header comment at the top of motion.js, change `numbers and rings that animate on first sight` to `numbers, rings and lines that animate the first time they are on screen`.

- [ ] **Step 5: Run the tests and the affected classes**

Run: `dotnet test tests/GymTracker.E2E.Tests --filter "FullyQualifiedName~MotionPolishTests|FullyQualifiedName~VisualSystemTests|FullyQualifiedName~AiPathTests|FullyQualifiedName~WorkoutFlowTests|FullyQualifiedName~SmokeTests"`
Expected: PASS. Then Core and Web: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/GymTracker.Web/wwwroot/js/pages.js src/GymTracker.Web/wwwroot/js/motion.js tests/GymTracker.E2E.Tests/MotionPolishTests.cs
git commit -m "Start count-ups, rings and line draws when they come on screen

They all ran at load, so anything below the fold had finished before it
was seen. gymWhenSeen (pages.js) starts each one when its top comes
within 85 % of the viewport, as apple.com's staggered reveals do."
```

---

### Task 3: Blocks below the fold rise in on first sight

**Files:**
- Modify: `src/GymTracker.Web/wwwroot/js/motion.js` (new `riseBelowFold`; the first `reveal` call; the `gym:page` handler)
- Modify: `src/GymTracker.Web/wwwroot/css/app.css` (motion section: the `#main.is-entering` rule, new rise rules)
- Test: `tests/GymTracker.E2E.Tests/MotionPolishTests.cs`

**Interfaces:**
- Consumes: `window.gymWhenSeen`, `firstSight()`, `reveal(scope, animate)` from Task 2; `FirstSightAsync`, `SeedWeightsAsync`, `ShortScreen` from Task 2.
- Produces: classes `is-pending-rise` and `is-rising` on direct children of `#main`, and the custom property `--rise-delay`.

- [ ] **Step 1: Write the failing tests**

Add to `MotionPolishTests`:

```csharp
    private const string BodySection = "#main > section:has(.spark)";

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Blocks_below_the_fold_rise_in_once(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await SeedWeightsAsync(phone);
        await phone.Page.SetViewportSizeAsync(390, ShortScreen);
        await FirstSightAsync(phone, "/today");

        var body = phone.Page.Locator(BodySection);
        (await body.EvaluateAsync<bool>("e => e.getBoundingClientRect().top >= innerHeight")).Should().BeTrue("the test needs the Body section below the fold");
        (await body.GetAttributeAsync("class")).Should().Contain("is-pending-rise");
        (await body.EvaluateAsync<string>("e => getComputedStyle(e).opacity")).Should().Be("0");

        await body.ScrollIntoViewIfNeededAsync();
        await phone.Page.WaitForFunctionAsync($$"""
            () => { const b = document.querySelector("{{BodySection}}");
                    return !b.classList.contains("is-pending-rise") && !b.classList.contains("is-rising") && getComputedStyle(b).opacity === "1"; }
            """);

        await phone.GoAsync("/today");
        (await phone.Page.Locator(".is-pending-rise").CountAsync()).Should().Be(0, "a page rises in on its first sight in a session only");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Leaving_a_page_with_blocks_still_waiting_leaves_nothing_hidden_on_screen(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await SeedWeightsAsync(phone);
        await phone.Page.SetViewportSizeAsync(390, ShortScreen);
        await FirstSightAsync(phone, "/today");
        (await phone.Page.Locator(".is-pending-rise").CountAsync()).Should().BeGreaterThan(0);

        await phone.NavigateByAsync(() => phone.Page.Locator(".tabbar a[href='/food']").TapAsync());
        (await phone.Page.EvaluateAsync<bool>("() => [...document.querySelectorAll('.is-pending-rise')].every(e => e.getBoundingClientRect().top >= innerHeight)"))
            .Should().BeTrue("only blocks below the fold may wait, invisible, to rise in");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_screen_taller_than_the_page_hides_nothing(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.Page.SetViewportSizeAsync(390, 3000);
        await FirstSightAsync(phone, "/today");
        (await phone.Page.Locator(".is-pending-rise").CountAsync()).Should().Be(0);
        phone.ShouldHaveNoErrors();
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/GymTracker.E2E.Tests --filter "FullyQualifiedName~MotionPolishTests.Blocks_below|FullyQualifiedName~MotionPolishTests.Leaving_a_page|FullyQualifiedName~MotionPolishTests.A_screen_taller"`
Expected: `Blocks_below_the_fold_rise_in_once` FAILS (class missing), `Leaving_a_page...` FAILS (count is 0). `A_screen_taller_than_the_page_hides_nothing` PASSES (a guard, like the reduced-motion one).

- [ ] **Step 3: Add `riseBelowFold` and call it on first sight**

In `motion.js`, directly after the `reveal` function (before the `reveal(document, firstSight());` line), add:

```js

    // Blocks that start below the fold rise in as they come into view, on a page's first sight only (apple.com's
    // StaggeredFadeIn, smaller and quicker for a tool used every day). The class is only ever added here, so the page
    // shows in full if this script fails.
    function riseBelowFold(main) {
        Array.from(main.children).forEach((block) => {
            if (block.getBoundingClientRect().top < window.innerHeight) {
                return;
            }
            block.classList.add("is-pending-rise");
            window.gymWhenSeen(block, (el, index) => {
                el.style.setProperty("--rise-delay", `${index * 60}ms`);
                el.classList.replace("is-pending-rise", "is-rising");
                const done = (event) => {
                    if (event.target !== el) {
                        return;
                    }
                    el.removeEventListener("animationend", done);
                    el.classList.remove("is-rising");
                    el.style.removeProperty("--rise-delay");
                };
                el.addEventListener("animationend", done);
            });
        });
    }
```

Replace the line `    reveal(document, firstSight());` with:

```js
    const firstLoad = firstSight();
    reveal(document, firstLoad);
    if (firstLoad && document.getElementById("main")) {
        riseBelowFold(document.getElementById("main"));
    }
```

In the `gym:page` handler, replace:

```js
        reveal(main, firstSight());
```

with:

```js
        const first = firstSight();
        reveal(main, first);
        if (first) {
            riseBelowFold(main);
        }
```

Children hidden with `display: none` (the undo and scroll-to markers) have a top of 0 and are skipped by the `top < innerHeight` check.

- [ ] **Step 4: Add the rise styles and keep the soft-navigation stagger off waiting blocks**

In `app.css`, replace:

```css
#main.is-entering > * { animation: appear 320ms var(--ease-snappy) both; animation-delay: calc(var(--i, 0) * 24ms); }
```

with:

```css
#main.is-entering > :not(.is-pending-rise, .is-rising) { animation: appear 320ms var(--ease-snappy) both; animation-delay: calc(var(--i, 0) * 24ms); }

/* Below-the-fold blocks on a page's first sight (motion.js): hidden until they come into view, then they rise in,
   staggered by --rise-delay. will-change only while waiting or moving, as apple.com does. */
.is-pending-rise { opacity: 0; transform: translateY(16px); will-change: opacity, transform; }
.is-rising { animation: rise-in 420ms var(--ease-gentle) var(--rise-delay, 0ms) both; will-change: opacity, transform; }
@keyframes rise-in { from { opacity: 0; transform: translateY(16px); } }
```

- [ ] **Step 5: Run the tests and the affected classes**

Run: `dotnet test tests/GymTracker.E2E.Tests --filter "FullyQualifiedName~MotionPolishTests|FullyQualifiedName~NavigationTests|FullyQualifiedName~LayoutTests|FullyQualifiedName~AiPathTests|FullyQualifiedName~UntestedAreasTests|FullyQualifiedName~SmokeTests"`
Expected: PASS. Then Core and Web: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/GymTracker.Web/wwwroot/js/motion.js src/GymTracker.Web/wwwroot/css/app.css tests/GymTracker.E2E.Tests/MotionPolishTests.cs
git commit -m "Let blocks below the fold rise in as they come into view

On a page's first sight in a session, blocks that start below the fold
wait hidden and rise in (16 px, 420 ms, 60 ms apart) when seen. Taken
from apple.com's StaggeredFadeIn, smaller and quicker."
```

---

### Task 4: Progress charts draw in when seen

**Files:**
- Modify: `src/GymTracker.Web/wwwroot/js/progress.js`
- Test: `tests/GymTracker.E2E.Tests/MotionPolishTests.cs`

**Interfaces:**
- Consumes: `window.gymWhenSeen` (Task 2), `FirstSightAsync`, `ShortScreen` (Task 2), `Workouts.WithTargetsAsync`, `Workouts.PostAsync`.
- Produces: nothing used later.

- [ ] **Step 1: Write the failing tests**

Add to `MotionPolishTests`:

```csharp
    private const string CaloriesChartRunning = "() => Chart.animator.running(Chart.getChart(document.getElementById('calories-chart')))";

    /// <summary>A target and one day of food, so Progress has a Calories chart (lower on the page).</summary>
    private async Task WithCaloriesAsync(Phone phone)
    {
        await phone.WithTargetsAsync(app);
        await phone.GoAsync("/today"); // stores today's estimate, the first day with a target
        await phone.GoAsync("/food");
        var quick = await phone.Page.Locator("[data-sheet-open='quick-sheet']").First.GetAttributeAsync("hx-get");
        var date = System.Text.RegularExpressions.Regex.Match(quick!, @"date=([0-9-]+)").Groups[1].Value;
        await phone.PostAsync("/food/quick", new() { ["date"] = date, ["meal"] = "Lunch", ["kcal"] = "600", ["protein"] = "40" });
    }

    private static Task TwoFramesAsync(Phone phone) =>
        phone.Page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))");

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_chart_below_the_fold_draws_in_when_scrolled_to(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await WithCaloriesAsync(phone);
        await phone.Page.SetViewportSizeAsync(390, ShortScreen);
        await FirstSightAsync(phone, "/progress");
        await phone.Page.WaitForFunctionAsync("() => window.Chart && Chart.getChart(document.getElementById('calories-chart'))");

        var canvas = phone.Page.Locator("#calories-chart");
        (await canvas.EvaluateAsync<bool>("e => e.getBoundingClientRect().top > innerHeight")).Should().BeTrue("the test needs the chart below the fold");
        (await phone.Page.EvaluateAsync<bool>(CaloriesChartRunning)).Should().BeFalse("the chart draws in when it is seen, not at load");

        await canvas.ScrollIntoViewIfNeededAsync();
        await phone.Page.WaitForFunctionAsync(CaloriesChartRunning);
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_theme_change_before_a_chart_is_seen_redraws_it_quietly(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await WithCaloriesAsync(phone);
        await phone.Page.SetViewportSizeAsync(390, ShortScreen);
        await FirstSightAsync(phone, "/progress");
        await phone.Page.WaitForFunctionAsync("() => window.Chart && Chart.getChart(document.getElementById('calories-chart'))");

        await phone.Page.EvaluateAsync("() => { document.documentElement.dataset.theme = 'light'; }");
        await TwoFramesAsync(phone);
        await phone.Page.Locator("#calories-chart").ScrollIntoViewIfNeededAsync();
        await TwoFramesAsync(phone);
        (await phone.Page.EvaluateAsync<bool>(CaloriesChartRunning)).Should().BeFalse("a redraw for the theme has no entrance");
        phone.ShouldHaveNoErrors();
    }
```

- [ ] **Step 2: Run them to verify the first fails**

Run: `dotnet test tests/GymTracker.E2E.Tests --filter "FullyQualifiedName~MotionPolishTests.A_chart_below|FullyQualifiedName~MotionPolishTests.A_theme_change"`
Expected: `A_chart_below_the_fold_draws_in_when_scrolled_to` FAILS: either `running` is true at load, or, if its 900 ms entrance already ended, the wait after scrolling times out. `A_theme_change...` may pass already (the redraw never animates); it guards the new deferred path.

- [ ] **Step 3: Defer each chart's entrance until it is seen**

In `progress.js`, replace each of the five lines

```js
                    animation: motion,
```

with

```js
                    animation: false,
```

(one each in the weight, e1RM, calories, protein and volume charts).

Then, at the end of `draw(animate)`, after the volume chart's closing `}` and before the function's own closing `}`, add:

```js

        charts.forEach((chart) => enter(chart, motion));
```

Above `function draw(animate) {`, add:

```js
    // A chart's entrance plays when the chart comes into view (pages.js gymWhenSeen), not at load, so charts lower on
    // the page haven't finished before they are seen. The chart is drawn at once without it, then reset and replayed.
    function enter(chart, motion) {
        if (!motion) {
            return;
        }
        window.gymWhenSeen(chart.canvas, () => {
            if (!charts.includes(chart)) {
                return; // redrawn for a theme change, or the page was left
            }
            chart.config.options.animation = motion;
            chart.reset();
            chart.update();
        });
    }
```

Change the header comment's second sentence to: `Series draw in the first time the page is seen in a session only, each chart as it comes into view.`

A chart already on screen at load gets its callback synchronously right after it is created, in the same task, so no finished frame is painted before the entrance.

- [ ] **Step 4: Run the tests and the affected classes**

Run: `dotnet test tests/GymTracker.E2E.Tests --filter "FullyQualifiedName~MotionPolishTests|FullyQualifiedName~ChartTests"`
Expected: PASS. Then Core and Web: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/GymTracker.Web/wwwroot/js/progress.js tests/GymTracker.E2E.Tests/MotionPolishTests.cs
git commit -m "Play each Progress chart's entrance when it comes into view

All charts animated at load, so the lower ones had finished before they
were seen. Charts are still created at load; the entrance is reset and
replayed once the canvas is on screen."
```

---

### Task 5: Row animations scale with height and fit the swap

**Files:**
- Modify: `src/GymTracker.Web/wwwroot/js/motion.js` (`appear`, `collapse`, the `htmx:beforeSwap` listener)
- Test: `tests/GymTracker.E2E.Tests/MotionPolishTests.cs`

**Interfaces:**
- Produces: `motionDuration(height: number): number` in motion.js, exposed as `window.gymMotionDuration`; `swapDelay(el: Element | null): number`; `collapse(el, maxMs = Infinity): Promise<void>` (`window.gymCollapse` keeps its name).

- [ ] **Step 1: Write the failing test**

Add to `MotionPolishTests`:

```csharp
    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Row_animation_length_follows_height(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press");
        // Records the duration of every Web Animation, with the class of the element it runs on.
        await phone.Context.AddInitScriptAsync("""
            window.__animations = [];
            const animate = Element.prototype.animate;
            Element.prototype.animate = function (keyframes, options) {
                window.__animations.push({ el: String(this.className), duration: typeof options === "number" ? options : (options && options.duration) || 0 });
                return animate.call(this, keyframes, options);
            };
            """);
        await phone.StartSessionAsync(template);

        (await phone.Page.EvaluateAsync<double[]>("() => [44, 200, 800].map((h) => window.gymMotionDuration(h))")).Should().Equal(240, 300, 480);

        // Removing an empty row: the server answers with nothing and htmx swaps the row out after 180 ms (_SetRow.cshtml).
        var rows = phone.Page.Locator(".exercise").First.Locator(".rows.working .set-row");
        var before = await rows.CountAsync();
        await rows.Nth(before - 1).Locator(".set-delete").EvaluateAsync("b => b.click()");
        await phone.Page.WaitForFunctionAsync("n => document.querySelectorAll('.exercise .rows.working .set-row').length === n - 1", before);
        var collapses = await phone.Page.EvaluateAsync<double[]>("() => window.__animations.filter((a) => a.el.includes('set-row')).map((a) => a.duration)");
        collapses.Should().NotBeEmpty().And.OnlyContain(d => d <= 180, "the row must finish collapsing before htmx swaps it out at 180 ms");
        phone.ShouldHaveNoErrors();
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/GymTracker.E2E.Tests --filter "FullyQualifiedName~MotionPolishTests.Row_animation"`
Expected: FAIL, `window.gymMotionDuration is not a function`.

- [ ] **Step 3: Scale appear and collapse, and cap collapse at the swap delay**

In `motion.js`, replace from `function appear(el) {` through the `htmx:beforeSwap` listener (ending `    });` after `collapse(event.detail.target);`), keeping the `htmx:afterSwap` listener in between unchanged, with:

```js
    // Longer moves take longer, as apple.com's expanding trays do (there clamp(h × 2/3, 400, 800) ms), at this app's
    // tempo: a single row about 240 ms, a tall block up to 480 ms.
    function motionDuration(height) {
        return Math.round(Math.min(480, Math.max(240, 200 + height / 2)));
    }
    window.gymMotionDuration = motionDuration;

    function appear(el) {
        if (reduce.matches || !(el instanceof HTMLElement)) {
            return;
        }
        const h = el.offsetHeight;
        el.animate([{ height: "0px", opacity: 0, overflow: "hidden" }, { height: `${h}px`, opacity: 1, overflow: "hidden" }],
            { duration: motionDuration(h), easing: snappy() });
    }
    window.gymAppear = appear;

    // A collapse runs inside htmx's swap delay; longer than that and the row is replaced before it has finished.
    function collapse(el, maxMs = Infinity) {
        if (reduce.matches || !(el instanceof HTMLElement)) {
            return Promise.resolve();
        }
        const h = el.offsetHeight;
        const duration = Math.min(maxMs, Math.round(motionDuration(h) * 0.75));
        if (duration <= 0) {
            return Promise.resolve();
        }
        return el.animate([{ height: `${h}px`, opacity: 1, overflow: "hidden" }, { height: "0px", opacity: 0, overflow: "hidden" }],
            { duration, easing: "cubic-bezier(.4,0,.2,1)", fill: "forwards" }).finished;
    }
    window.gymCollapse = collapse;

    // The delay in an hx-swap such as "outerHTML swap:180ms", or 0 when the swap is immediate.
    function swapDelay(el) {
        const match = /swap:(\d+)ms/.exec((el && el.getAttribute("hx-swap")) || "");
        return match ? Number(match[1]) : 0;
    }

    document.addEventListener("htmx:afterSwap", (event) => {
        const swap = event.detail.requestConfig && event.detail.requestConfig.elt && event.detail.requestConfig.elt.getAttribute("hx-swap");
        if (swap && swap.startsWith("beforeend")) {
            const target = event.detail.target;
            appear(target.lastElementChild);
        }
    });

    // Rows deleted through htmx collapse during the swap delay (the set rows use hx-swap="outerHTML swap:180ms").
    document.addEventListener("htmx:beforeSwap", (event) => {
        if (event.detail.xhr.status === 200 && event.detail.serverResponse.trim() === "" && event.detail.target.matches(".set-row, .row, li")) {
            const trigger = event.target instanceof Element ? event.target.closest("[hx-swap]") : null;
            collapse(event.detail.target, swapDelay(trigger));
        }
    });
```

`htmx:beforeSwap` is dispatched on the element that made the request (the Delete or Remove button), which carries the `hx-swap` attribute.

- [ ] **Step 4: Run the test and the affected classes**

Run: `dotnet test tests/GymTracker.E2E.Tests --filter "FullyQualifiedName~MotionPolishTests|FullyQualifiedName~SessionTests|FullyQualifiedName~UndoToastTests|FullyQualifiedName~WorkoutFlowTests"`
Expected: PASS. Then Core and Web: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/GymTracker.Web/wwwroot/js/motion.js tests/GymTracker.E2E.Tests/MotionPolishTests.cs
git commit -m "Scale row animations with height and fit collapses in the swap

Appear and collapse used fixed 340 and 260 ms. They now scale with the
row's height (240 to 480 ms), and a collapse never outlasts its htmx
swap delay: set rows swap at 180 ms and were cut off mid-collapse."
```

---

### Task 6: Faded edges on scroll areas

**Files:**
- Modify: `src/GymTracker.Web/wwwroot/css/app.css` (new rules after the `.change-food-results` rule, around line 720)
- Modify: `src/GymTracker.Web/wwwroot/js/motion.js` (new section after "instant filter for long lists")
- Modify: `src/GymTracker.Web/Views/Food/Confirm.cshtml:104`
- Modify: `src/GymTracker.Web/Views/Onboarding/Index.cshtml:83`
- Test: `tests/GymTracker.E2E.Tests/MotionPolishTests.cs`

**Interfaces:**
- Produces: classes `scroll-fade` plus `scroll-fade-x` or `scroll-fade-y`; inline custom properties `--fade-start` and `--fade-end`, each `transparent` (fade) or `#000` (no fade).

- [ ] **Step 1: Write the failing test**

Add to `MotionPolishTests`:

```csharp
    private static Task<string[]> FadesAsync(ILocator el) =>
        el.EvaluateAsync<string[]>("e => [e.style.getPropertyValue('--fade-start'), e.style.getPropertyValue('--fade-end')]");

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Scroll_fades_follow_the_scroll_position(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/food/add?meal=Lunch&tab=text");
        await phone.Page.Locator("textarea[name=text], input[name=text]").First.FillAsync("150 g cooked rice");
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/parse'] button[type=submit]").First.TapAsync());
        await phone.Page.Locator(".result").First.Locator("[data-change-food]").TapAsync();

        var search = phone.Page.Locator("#change-food-sheet [data-change-search]");
        var results = phone.Page.Locator("#change-food-results");
        // Matches this search only, not the one the sheet starts with for the row's name.
        async Task SearchAsync(string text)
        {
            await phone.Page.RunAndWaitForResponseAsync(() => search.FillAsync(text), r => r.Url.Contains("/food/candidates") && r.Url.EndsWith("q=" + Uri.EscapeDataString(text)));
            await phone.Page.WaitForFunctionAsync("() => !document.querySelector('#change-food-results.htmx-settling, #change-food-results.htmx-swapping')");
        }

        await SearchAsync("rice");
        (await results.EvaluateAsync<string>("e => { const s = getComputedStyle(e); return s.maskImage && s.maskImage !== 'none' ? s.maskImage : s.webkitMaskImage; }"))
            .Should().Contain("gradient");
        (await results.EvaluateAsync<bool>("e => e.scrollHeight > e.clientHeight + 16")).Should().BeTrue("the test needs a list that scrolls");
        (await FadesAsync(results)).Should().Equal("#000", "transparent");

        await results.EvaluateAsync("e => e.scrollTo(0, e.scrollHeight)");
        await phone.Page.WaitForFunctionAsync("() => document.querySelector('#change-food-results').style.getPropertyValue('--fade-end') === '#000'");
        (await FadesAsync(results)).Should().Equal("transparent", "#000");

        // Nothing to scroll: neither edge fades.
        await SearchAsync("zzqxv");
        (await FadesAsync(results)).Should().Equal("#000", "#000");
        phone.ShouldHaveNoErrors();
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/GymTracker.E2E.Tests --filter "FullyQualifiedName~MotionPolishTests.Scroll_fades"`
Expected: FAIL, the mask is `none` (the assertion on "gradient").

- [ ] **Step 3: Add the fade styles**

In `app.css`, after the line `.change-food-results { max-height: 55vh; overflow-y: auto; overscroll-behavior: contain; }`, add:

```css
/* Faded edges on scroll areas, as apple.com's scroll masks: motion.js sets --fade-start and --fade-end to transparent
   while there is more to scroll that way, so an edge fades only when content continues past it. */
.scroll-fade-x {
    -webkit-mask-image: linear-gradient(to right, var(--fade-start, #000) 0, #000 16px, #000 calc(100% - 16px), var(--fade-end, #000) 100%);
    mask-image: linear-gradient(to right, var(--fade-start, #000) 0, #000 16px, #000 calc(100% - 16px), var(--fade-end, #000) 100%);
}
.scroll-fade-y {
    -webkit-mask-image: linear-gradient(to bottom, var(--fade-start, #000) 0, #000 16px, #000 calc(100% - 16px), var(--fade-end, #000) 100%);
    mask-image: linear-gradient(to bottom, var(--fade-start, #000) 0, #000 16px, #000 calc(100% - 16px), var(--fade-end, #000) 100%);
}
```

- [ ] **Step 4: Keep the fades in step with the scroll position**

In `motion.js`, after the "instant filter for long lists" section (before `// ---- toast ----`), add:

```js
    // ---- faded edges on scroll areas: an edge fades only while there is more past it ----

    function updateFade(el) {
        const x = el.classList.contains("scroll-fade-x");
        const position = x ? el.scrollLeft : el.scrollTop;
        const max = x ? el.scrollWidth - el.clientWidth : el.scrollHeight - el.clientHeight;
        el.style.setProperty("--fade-start", position > 1 ? "transparent" : "#000");
        el.style.setProperty("--fade-end", position < max - 1 ? "transparent" : "#000");
    }

    // Size changes cover a sheet opening (its list goes from no box to a box); content swaps are handled below.
    const fadeResize = "ResizeObserver" in window ? new ResizeObserver((entries) => entries.forEach((entry) => updateFade(entry.target))) : null;
    function bindFades(scope) {
        $$(".scroll-fade", scope).forEach((el) => {
            if (!el.gymFade) {
                el.gymFade = true;
                el.addEventListener("scroll", () => updateFade(el), { passive: true });
            }
            if (fadeResize) {
                fadeResize.observe(el);
            }
            updateFade(el);
        });
    }
    bindFades(document);
    document.addEventListener("gym:beforeswap", () => fadeResize && fadeResize.disconnect());
    document.addEventListener("gym:page", () => bindFades(document));
    document.addEventListener("htmx:afterSwap", (event) => {
        const el = event.detail.target.closest(".scroll-fade");
        if (el) {
            updateFade(el);
        }
    });
```

- [ ] **Step 5: Mark the two scroll areas**

In `Views/Food/Confirm.cshtml`, replace:

```html
            <div id="change-food-results" class="change-food-results"></div>
```

with:

```html
            <div id="change-food-results" class="change-food-results scroll-fade scroll-fade-y"></div>
```

In `Views/Onboarding/Index.cshtml`, replace:

```html
                        <div class="reply-chips">
```

with:

```html
                        <div class="reply-chips scroll-fade scroll-fade-x">
```

- [ ] **Step 6: Run the test and the affected classes**

Run: `dotnet test tests/GymTracker.E2E.Tests --filter "FullyQualifiedName~MotionPolishTests|FullyQualifiedName~ConfirmFoodsTests|FullyQualifiedName~AiPathTests|FullyQualifiedName~AccountTests"`
Expected: PASS. Then Core and Web: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/GymTracker.Web/wwwroot/css/app.css src/GymTracker.Web/wwwroot/js/motion.js src/GymTracker.Web/Views/Food/Confirm.cshtml src/GymTracker.Web/Views/Onboarding/Index.cshtml tests/GymTracker.E2E.Tests/MotionPolishTests.cs
git commit -m "Fade the edges of the setup chips and the Change food results

Both cut off hard, so it wasn't clear they scroll. An edge now fades
over 16 px while there is more content past it, as apple.com's scroll
masks do."
```

---

### Task 7: Service worker, full suites and a look at 390 px

**Files:**
- Modify: `src/GymTracker.Web/wwwroot/sw.js:4`
- Create, run and delete (not committed): `tests/GymTracker.E2E.Tests/MotionShotsTemp.cs`

- [ ] **Step 1: Bump the static cache**

In `sw.js`, replace `const CACHE = "gym-static-v24";` with `const CACHE = "gym-static-v25";`.

- [ ] **Step 2: Build and run everything**

Run: `dotnet build && dotnet test`
Expected: build with no warnings added by this work; Core, Web and the full browser suite PASS. Report the actual counts. If a browser test outside `MotionPolishTests` fails because an element is now `is-pending-rise` or still `is-rising` (opacity 0 or moved) when the test reads it, fix the test to wait for `phone.AnimationsDoneAsync(selector)` or to scroll the element into view first, not the product code, and name each such test in the commit.

- [ ] **Step 3: Take screenshots for a visual check**

Create `tests/GymTracker.E2E.Tests/MotionShotsTemp.cs` (it is deleted again in Step 4):

```csharp
using GymTracker.Core.Domain;
using GymTracker.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.E2E.Tests;

public class MotionShotsTemp(E2EFixture app) : IClassFixture<E2EFixture>
{
    [Fact]
    public async Task Shots()
    {
        var dir = Environment.GetEnvironmentVariable("GYM_SHOTS") ?? Path.GetTempPath();
        foreach (var theme in new[] { "dark", "light" })
        {
            await using var phone = await app.SignedInPhoneAsync(E2EFixture.WebKit);
            await using (var db = app.Db.CreateContext(null))
            {
                var id = await db.Users.Where(u => u.Email == phone.Email).Select(u => u.Id).SingleAsync();
                var today = UserClock.Today(DateTimeOffset.UtcNow, UserProfile.DefaultTimeZoneId);
                for (var i = 0; i < 5; i++)
                {
                    db.Bodyweights.Add(new BodyweightEntry { UserId = id, Date = today.AddDays(-i), WeightKg = 80m + i * 0.3m });
                }

                await db.SaveChangesAsync();
            }

            await phone.Page.EvaluateAsync($"() => {{ sessionStorage.clear(); document.documentElement.dataset.theme = '{theme}'; }}");
            await phone.GoAsync("/today");
            await phone.Page.EvaluateAsync($"() => {{ document.documentElement.dataset.theme = '{theme}'; }}");
            await phone.Page.EvaluateAsync("() => window.scrollTo(0, 420)");
            await phone.Page.WaitForTimeoutAsync(200); // mid-rise
            await phone.Page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"today-rise-{theme}.png") });
            await phone.Page.WaitForTimeoutAsync(800);
            await phone.Page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"today-settled-{theme}.png") });

            var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press", "Dip");
            await phone.GoAsync($"/train/templates/{template}");
            await phone.Page.EvaluateAsync($"() => {{ document.documentElement.dataset.theme = '{theme}'; }}");
            await phone.Page.Locator(".topbar-trailing [data-menu-trigger]").TapAsync();
            await phone.Page.WaitForTimeoutAsync(60); // items mid-stagger
            await phone.Page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"menu-opening-{theme}.png") });
        }
    }
}
```

Run, with `$SHOTS` a new folder in the session scratchpad: `GYM_SHOTS=$SHOTS dotnet test tests/GymTracker.E2E.Tests --filter "FullyQualifiedName~MotionShotsTemp"` (create the folder first). Open each PNG and check at 390 px, in both themes: the rising block is partly faded and offset, not missing or clipped; the settled page matches main's layout; the menu items are mid-stagger inside the panel, and nothing leaves the screen.

- [ ] **Step 4: Remove the screenshot test and commit**

```bash
rm tests/GymTracker.E2E.Tests/MotionShotsTemp.cs
git add src/GymTracker.Web/wwwroot/sw.js
git commit -m "Bump the static cache for the motion polish CSS and JS"
```

If Step 2 needed test fixes, commit them separately first, with the reason in the message.

- [ ] **Step 5: Hand over**

Report the test counts from Step 2, describe the screenshots, and use superpowers:finishing-a-development-branch for the merge. Deploying and the What's new entry (spec "Release") happen only once Antoan approves the deploy.
