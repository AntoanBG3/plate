# Add food: mode tabs

Short design for replacing the picker on `/food/add` and the motion between its five modes. No server changes, no new libraries, no new strings.

## Why

Antoan, 2026-11-14: the picker looks bad and the animations look cheap. Both were confirmed locally in WebKit at 390 px in Bulgarian.

- The picker is the shared segmented control with five equal segments. Each is 69 px wide and "Скорошни" is 69 px of text, so the 13 px semibold labels touch the edges under a heavy grey thumb.
- A tap on a segment is treated as going to a sibling page (`data-slide`, `segmentSide` in nav.js). The whole screen slides a full width for 280 ms, including the title and the picker, which are the same on both sides, and the thumb's own glide is turned off for that case (`app.css`, the next/prev rule).

## Goals and non-goals

The labels are readable with room to spare in both languages at 320 and 390 px. Switching mode reads as changing the contents of one page: the header and picker hold still, the selection indicator glides, the content changes in place.

Not changing: the five modes, their order, their `?tab=` URLs, the contents of each mode, the segmented control everywhere else (Settings, Progress, Feedback, onboarding), and history (each switch is still a history entry).

It would be a failure if a label were cut off or wrapped, if a tap target fell under 44 px, or if the new motion played on top of a page push or pop.

## Approaches

Mocked side by side in `design-samples/add-picker.html`.

- A, capsule: the segmented control refined, segments sized to their words. Consistent with the rest of the app, but the labels stay at 13 px with 9 px of padding.
- B, tiles: an icon over a small label. Repeats the tab bar's pattern on the same screen.
- C, underline: plain words at 15 px, an accent bar under the selected one, no track. Chosen by Antoan: the only option where the labels get a readable size. Its cost is a component used on this page only.

## Design

**Markup.** `Add.cshtml` replaces the `nav.segmented` with `nav.mode-tabs` (same `aria-label`). Each mode is `a.mode-tab` with the same `href` and `aria-current="page"` on the selected one, which also has `is-active`. The selected tab contains one `i.mode-bar` (`aria-hidden`), rendered only there.

**Look.** A row with `justify-content: space-between`, a 0.5 px separator along its bottom, and the first and last words aligned with the page gutters. Labels are semibold, `--label-2`, and `--label` when selected. The font size is `min(var(--text-subhead), 3.85vw)`: 15 px at 390 px, 12.3 px at 320 px, and never larger with Dynamic Type, so five Bulgarian words always fit on one line. Each tab is 44 px tall and its hit area reaches halfway into the gaps on either side. The bar is 3 px of `--accent` with rounded top corners, as wide as the word, sitting on the separator. Focus shows the app's usual accent outline. Both themes use the same tokens.

**Motion.** `data-slide` is removed, so nav.js classes a mode switch as `swap` (same path, other query), the kind the Progress range already uses: old and new content crossfade in place over `--dur-swap` and the title and top bar are left alone. The bar gets `view-transition-name: mode-bar` only while `html[data-nav]` is `swap` or `same`, and its group runs for `--dur-page` on `--ease-snappy`, so it glides and resizes to the new word. On every other kind of navigation (pushing into the page, Back, a tab bar change) the bar is unnamed and travels with its page. Reduced motion already maps to the plain fade with no glide.

**Clean-up.** Nothing else uses `data-slide`, so `segmentSide` and the `side` option go from nav.js, and the comments in app.css and nav.js that describe the sideways slide for segmented controls are corrected. The next/prev slide itself stays for the tab bar.

**SPEC.** Section 2's UI row describes page changes; it gets one sentence saying the add-food modes change in place.

## Tests

- E2E, Chromium and WebKit, 320 and 390 px, English and Bulgarian: no mode tab's label is wider than its tab or wraps, the row doesn't scroll sideways, and every tab's hit area is at least 44 px tall.
- E2E: tapping a mode sets `data-nav="swap"`, the URL and selected tab change, and the title's position is the same before and after; the bar is named `mode-bar` during the swap and unnamed when the page is pushed.
- The label-fit test in `LayoutTests` keeps covering `.segmented` on the other pages.
- `design-samples/tools/motion-record.mjs`: the `segment-slide` scenario and tour step point at the new tabs.

## Left to the builder

Exact gap and padding values, the separator's inset, and whether the hit-area extension is a pseudo-element or padding.
