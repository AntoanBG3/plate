# Motion polish from apple.com

Design for four refinements to the existing visuals and motion, taken from an inspection of apple.com on 2026-10-11. No new screens, no new libraries, no server changes.

## How the findings were gathered

apple.com's home and iPhone pages were captured with REA (`rea-agents` 6.4.0, `inspect-web-page`, `analyze-web-bundle`, `export-web-scripts`) against an isolated Chrome for Testing profile, plus WebKit screenshots at 390 px and 1440 px of the home, iPhone, Fitness+ and Store pages. Apple ships no source maps, so the conclusions come from the minified scripts and the 1.3 MB of computed CSS. The captures live in the session scratchpad and are not committed.

Checked and already matching, so out of scope: the top bar and tab bar material (Apple's global nav uses the same `saturate(180%) blur(20px)` over a roughly 0.8 alpha surface), tracking (the app follows the iOS HIG table, which is right for SF Pro in an app; apple.com's web values differ slightly), and page transitions (apple.com uses no View Transitions; the app's push, pop and tab system goes further). Also left out on purpose: pill buttons, scroll-scrubbed video, and apple.com's marketing type scale.

## 1. Closed menus leave the accessibility tree

**Finding.** Apple's nav flyouts hide with `visibility: hidden` timed by the transition: `visibility 0.24s step-end` when closing, so the element stays visible for the fade and then becomes hidden; `step-start` when opening, so it is visible from the first frame. Flyout items enter with a short slide and fade (`translate(8px)` to `0`, opacity `0` to `1`, 240 ms, `cubic-bezier(0.4, 0, 0.6, 1)`).

**Problem in the app.** `.menu` (app.css, context menu) is hidden only by `opacity: 0` and `pointer-events: none`. Its buttons and links stay in the tab order and are read by VoiceOver while the menu is closed. This affects all five `data-menu-trigger` menus: the exercise card, a meal on the Food day, the template editor (page and exercise), and the templates list.

**Change.**
- `.menu` gets `visibility: hidden` with `transition: … visibility 0s linear 300ms` (hidden after the close animation); `.menu.is-open` gets `visibility: visible` with no visibility delay. Same effect as Apple's step-end and step-start, written so it works with the existing spring transform.
- Items stagger in when the menu opens: each direct child of `.menu` rises from `translateY(-4px)` with opacity 0, 200 ms on `--ease-out`, delayed `calc(var(--i) * 18ms)` after a 40 ms lead. `--i` is set by CSS `:nth-child` for the first six children (no menu has more), so no JavaScript change is needed. Closing has no stagger: the panel fades as now.
- The keyboard focus code in motion.js keeps working, since `visibility` changes before the first item is focused (the menu is opened, then focused, in the same click handler; `visibility: visible` applies without delay).
- Reduced motion: the existing rule already drops the transform; the item stagger is removed.

## 2. First-sight animations wait until they are on screen

**Finding.** Apple's `StaggeredFadeIn` component (iPhone page, `overview/main.built.js`) is configured by CSS custom properties (`--staggered-delay: 0.15`, `--staggered-opacity-duration: 0.9`, `--staggered-translate-y: 30px`, `--staggered-translate-y-duration: 0.7`). It starts when its anchor's top reaches 85 vh (`start: "t - 85vh"`), applies `will-change` only while running, and is skipped entirely when `html.reduced-motion` is set. The hiding class is added by the script, so content is visible if the script fails.

**Problem in the app.** `reveal()` in motion.js runs every count-up (`data-count`), ring fill (`data-progress`) and line draw (`data-draw`) at page load, and progress.js draws every Progress chart's entrance at load. Anything below the fold (the weight trend card and its sparkline on Today, the lower charts on Progress) finishes animating before it is seen. Blocks below the fold also appear without any entrance, while the first eight blocks get the staggered rise on a soft navigation.

**Change.**
- `reveal()` keeps its once-per-page-per-session gate (`seenBefore`), but each animated element starts when it first comes within 85 % of the viewport height, using one shared `IntersectionObserver` with `rootMargin: "0px 0px -15% 0px"`. An element already in view at load starts immediately, as now. While an element waits, it shows its starting state (a count shows its `data-from` value, a ring or line is undrawn), so it does not jump from its final value back to the start when the animation begins. Most waiting elements sit inside a block that is also waiting to rise in (below), so the starting state is rarely seen.
- Progress charts: each chart's entrance animation runs when that chart's canvas first comes into view, with the same observer rule and the existing `gym.charts.seen` gate. Charts are still created at load (so layout and sizes don't change); only the animation is deferred. Chart.js supports this by creating the chart with `animation: false` and then calling `update()` with the entrance animation enabled once the canvas is in view.
- Below-the-fold blocks: direct children of `#main` that start below the fold on the first sight of a page get the class `is-pending-rise` from the script (opacity 0, `translateY(16px)`). As each one comes into view it rises in over 420 ms on `--ease-gentle`; blocks entering together are staggered 60 ms apart. `will-change: transform, opacity` is set only while a block has `is-pending-rise` or is rising, and removed on `animationend`. Values are deliberately smaller than Apple's 30 px and 0.9 s: this is a tool used several times a day, not a product page.
- This applies only when the existing first-sight gate allows animation. On later visits within the session, and with reduced motion, nothing is hidden and nothing moves. The class is only ever added by JavaScript, so content shows if motion.js fails.
- Soft navigation (`gym:page`) and the existing `#main.is-entering` stagger of the first eight blocks are unchanged. Blocks that are already rising in through `is-entering` are not also given `is-pending-rise`.

## 3. Row animations scale with height

**Finding.** Apple's expanding trays compute their duration from the distance travelled: `Math.min(Math.max(height / 3 * 2, 400), 800)` ms.

**Problem in the app.** `gymAppear` (340 ms) and `gymCollapse` (260 ms) use fixed durations, so a tall block moves noticeably faster than a single row. The collapse is also longer than the swap it runs inside: the set rows delete with `hx-swap="outerHTML swap:180ms"` (`_SetRow.cshtml`), so the row is replaced at 180 ms, before the 260 ms collapse has finished (at that point the eased collapse is roughly 85 % done, so the last part disappears in one frame). The motion.js comment still says `swap:260ms`.

**Change.** Same principle at the app's tempo:
- appear: `clamp(240, 200 + height / 2, 480)` ms, still on `--ease-snappy`.
- collapse: 75 % of the appear duration for the same height (180 to 360 ms), still on `cubic-bezier(.4, 0, .2, 1)`, but never longer than the swap delay read from the triggering element's `hx-swap` attribute (`swap:<n>ms`). For the set rows that keeps it at 180 ms and ends the jump. `gymCollapse` has no callers outside motion.js, so this is the only path.
- A small pure function `motionDuration(height)` in motion.js holds the formula and is exposed as `window.gymMotionDuration` for the test.
- The stale `swap:260ms` comment is corrected.

## 4. Faded edges on scroll areas

**Finding.** Apple fades scroll containers with a two-layer `mask-image`: a gradient from the scroll-mask colour to black over 16 px at each end. The ends are toggled by setting `--scroll-mask-top-color` or `--scroll-mask-bottom-color` to `transparent` only when there is content past that edge.

**Problem in the app.** The setup chat's reply chips (`.setup-dock .reply-chips`, horizontal, run to the screen edge) and the Change food results (`.change-food-results`, vertical, `max-height: 55vh`) cut off hard, so it is not obvious there is more to scroll.

**Change.**
- A `.scroll-fade` class (axis set by `.scroll-fade-x` or `.scroll-fade-y`) applies `mask-image: linear-gradient(to right, var(--fade-start, #000) 0, #000 16px, #000 calc(100% - 16px), var(--fade-end, #000) 100%)` (or `to bottom` for y), with `-webkit-mask-image` for Safari.
- motion.js sets `--fade-start: transparent` when the element is scrolled away from its start, and `--fade-end: transparent` when there is more content past its end, on `scroll` (passive) and when the element's size or content changes (`ResizeObserver`, plus `htmx:afterSwap` for the results list). Short content that does not scroll gets no fade.
- Applied to the reply chips and the Change food results. Not applied to bottom sheets: a sheet already shows its edge and grabber, and its content scrolls under the sheet head.
- Reduced motion does not affect this; it is static.

## Testing

Browser tests (Playwright, Chromium and WebKit, 390 px), written before each change and seen to fail first:

1. `MotionPolishTests.A_closed_menu_cannot_be_focused_or_read`: on a session with an exercise, pressing Tab from the exercise menu trigger skips the closed menu's items, and the menu's computed `visibility` is `hidden`. After the trigger is clicked, `visibility` is `visible` and the first item takes focus from the keyboard.
2. `MotionPolishTests.Below_the_fold_animations_wait_until_scrolled_into_view`: on the first sight of Today with a weight trend (fresh session, so the first-sight gate allows animation), the trend sparkline has no running animation at load. After it is scrolled into view it has one, and once that ends the trend value shows its final number. Elements above the fold (the kcal gauge) animate at load, as now.
3. `MotionPolishTests.Blocks_below_the_fold_rise_in_once`: on the first sight of Today, a block below the fold has `is-pending-rise` and opacity 0; after scrolling to it, the class is gone after its animation ends and opacity is 1. On a second visit in the same session no block gets the class.
4. `MotionPolishTests.Reduced_motion_hides_nothing`: with `prefers-reduced-motion: reduce`, no element on Today or Progress has `is-pending-rise`, and every `data-count` shows its final value at load.
5. `MotionPolishTests.Scroll_fades_follow_the_scroll_position`: in the Change food results with enough results to scroll, `--fade-end` is `transparent` and `--fade-start` is not at the top; after scrolling to the end, the reverse.
6. `MotionPolishTests.Row_animation_length_follows_height`: `window.gymMotionDuration` gives 44 → 240, 200 → 300, 800 → 480; deleting a logged set collapses the row in no more than its 180 ms swap delay (the row's collapse animation has finished, or was never longer, when the swap happens).

Existing tests that cover the touched code (UndoToastTests, NavigationTests, ChartTests, VisualSystemTests, SessionTests, TemplateEditorTests, ConfirmFoodsTests) must stay green. Then the full Core, Web and browser suites per the test cadence, and a visual check at 390 px in light and dark.

## Files

- `src/GymTracker.Web/wwwroot/css/app.css`: menu visibility and item stagger; `is-pending-rise` and its rise animation; `.scroll-fade`; reduced-motion rules.
- `src/GymTracker.Web/wwwroot/js/motion.js`: in-view observer for `reveal()`; below-the-fold rise; `motionDuration`; scroll fade updater.
- `src/GymTracker.Web/wwwroot/js/progress.js`: per-chart deferred entrance.
- `src/GymTracker.Web/Views/Onboarding/Index.cshtml`, `src/GymTracker.Web/Views/Food/Confirm.cshtml`: add the `scroll-fade` classes.
- `src/GymTracker.Web/wwwroot/sw.js`: bump `CACHE` (v24 to v25) so the precached unversioned copies of the changed CSS and JS are refreshed.
- `tests/GymTracker.E2E.Tests/MotionPolishTests.cs`: new.

## Release

User-visible but small. After deploy, one What's new entry covering the smoother scrolling animations and the fades on scroll areas; the menu fix is mentioned only as "menus behave better with VoiceOver and a keyboard".
