# Codebase Audit Report

**Plate · 21 November 2026 · reviewed revision `2644bbe`**

**Scope** — A sampled review of training, food entry, nutrition history, settings and export, Bulgarian localisation, and the associated Core, Web, browser and JavaScript tests. Browser reproductions used disposable accounts at 390 px. Performance figures below are local measurements or counts derived from the queries. Production latency and physical iPhone behaviour were outside this audit.

## Resolution on `changes/resolve-project-audit`

All 15 findings from the reviewed revision are addressed on this branch.

| Findings | Change | Verification |
|---|---|---|
| 1, 5 | Manual entry rejects malformed numbers and out-of-range portions. A rejected save keeps the submitted fields, barcode, sharing choice and label scan reference. | Browser tests submit a bare separator and a 3,001 g portion, bypass client validation, and check the returned form. Web tests verify rollback and retry. |
| 2 | Check foods has an account-scoped GET address backed by the original parse and a stable save ID. The tab retains edits to its eight most recent reviews. | Reload and Back restore an edited row in Chromium and WebKit. The Web test verifies that another account receives a 404. |
| 3, 9, 10 | The protein chart takes its minimum from each day's snapshot. Both chart history queries fetch the 28-day range and at most one preceding row. The initial backfill builds the weight trend once. | Yesterday's saved 95 g minimum survives today's 130 g target. For 1,095 weigh-ins, managed allocations for the trend calculation fell from 105,213,160 to 212,008 bytes with the same sum of daily estimates. |
| 4, 8 | Suggestions and “Last time” share the two most recent workouts with working sets per exercise. The pickers validate a whole selection before saving it. | A PostgreSQL command counter observed one history query for six exercise slots. Four intervening warm-up-only workouts preserved the last working sets. An archived pick leaves the original template intact. |
| 6 | The ZIP includes private foods, saved meals and their ordered items, exercise notes and bar settings, plus profile preferences. IDs link related rows. | An export test seeds two accounts and checks the contents and ownership of the new CSV files. |
| 7, 12 | Rest timer expiry dismisses only the rest sheet. Keyboard focus stays in the active dialog, including during async loads and nested confirmations. | Browser tests cover expiry with Plates open, Tab in both directions, and focus returning to the opener. |
| 11 | Each parsed food keeps its ranking, shortlist and decision together. Usual foods and private top matches skip the model shortlist. Switching the matcher off skips shortlist searches for every item. | Food matching and evaluation tests cover ranking, model questions, multi-item context and traces. |
| 13, 14, 15 | Bulgarian nutrition labels use the correct caption and units. Training screens preserve custom exercise names. The profile test suite keeps the time-zone case and the dedicated height and protein boundary theories. | Catalogue, Bulgarian browser and profile tests cover these cases. |

The local release gate on `4159a78` passed all 1,431 checks (55 JavaScript, 470 Core, 592 Web and 314 browser), with zero build warnings. Browser verification used Playwright Chromium and WebKit. The allocation benchmark excludes EF and JSON.

## Executive Summary

Food entry has two different failure modes. A mistyped nutrient can become a valid database value, while a rejected portion can erase the other fields the user entered. The food review URL also fails on reload in both browser engines. These are the first fixes I would ship.

Two screens can show misleading history even when the stored data is correct. Progress draws today's protein minimum over previous days. Train can forget an older working set after several workouts in which the exercise was skipped. Both fixes belong in the data supplied to the screen. The same training change can also remove repeated database queries on workout open.

## High-Priority Improvements

### 1. Manual food entry silently saves an invalid nutrient as zero

**Logic · High · Small fix.** In Chromium, entering `.` for protein, 100 kcal, 25 g carbs, 0 g fat and a 100 g portion passed browser validation. Save and add created a food-log row with **0 g protein**. The input pattern permits a separator with no digits. The decimal binder records an error, but the action passes the default decimal into the service. Require a digit, keep support for decimal commas, and return the submitted form with a field error whenever model binding fails. Check other decimal POST actions that accept zero. [Manual form](../../src/Plate.Web/Views/Food/_ManualFoodForm.cshtml#L60) · [Decimal binder](../../src/Plate.Web/Features/DecimalBinding.cs#L38) · [CreateItem](../../src/Plate.Web/Controllers/FoodController.cs#L192)

### 2. Reloading Check foods loses the review screen

**UX · High · Medium fix.** I parsed “150 g cooked rice”, reached Check foods, and reloaded. Both Chromium and WebKit returned **HTTP 404** at `/food/parse`. The parse action returns the review directly from a POST. Soft navigation puts that address in browser history, and reload requests it with GET. Give the review an account-scoped GET address and restore the draft from it without charging for another parse. Cover reload and Back, including a row the user edited before leaving. [Parse action](../../src/Plate.Web/Controllers/FoodController.cs#L81) · [History update](../../src/Plate.Web/wwwroot/js/nav.js#L303)

### 3. Progress draws today's protein minimum over historical days

**Logic · High · Small fix.** With yesterday's snapshot seeded at **95 g** and today's minimum at **130 g**, the chart data showed **130 g yesterday**. The historical aim is read per day, but the controller supplies one minimum and the view repeats it. Carry the minimum in each chart day from the same snapshot as its aim, including carried-forward days. Leave the value absent where no historical minimum is available. [Progress controller](../../src/Plate.Web/Controllers/ProgressController.cs#L29) · [Daily chart data](../../src/Plate.Web/Features/Body/BodyAnalytics.cs#L59) · [Chart series](../../src/Plate.Web/Views/Progress/Index.cshtml#L33)

### 4. Skipped exercises hide working history and trigger repeated queries

**Logic · High · Medium fix.** I recorded a bench working set, verified that Plate suggested a next set, then finished four sessions with bench in the template but no bench sets. The following session returned no suggestion. The query limits recent exercise appearances before discarding empty and warm-up-only sessions. “Last time” uses a shorter cutoff and can disappear after three skips.

The session page also makes both history passes separately. For six exercises with prior history, the loops issue **24 sequential history queries** before the other page reads. Select the last two distinct sessions containing a live working set per exercise, batch the query across exercise IDs, and use the result for both displays. [Session controller](../../src/Plate.Web/Controllers/TrainController.cs#L44) · [Last time](../../src/Plate.Web/Features/Analytics/TrainingAnalytics.cs#L125) · [History cutoff and set query](../../src/Plate.Web/Features/Analytics/TrainingAnalytics.cs#L195)

### 5. An invalid portion clears a completed manual food form

**UX · High · Medium fix.** I filled in the manual form and entered **3001 g**. Chromium accepted the input because `max` does not constrain a text field. The server refused the portion and redirected to a fresh form. Even the food name was empty. Redisplay the submitted fields, highlight the portion, and keep the label scan and sharing choice attached. Client-side range checks can give immediate feedback, while the server still needs to preserve the form on rejection. [Portion field](../../src/Plate.Web/Views/Food/_ManualFoodForm.cshtml#L69) · [Validation redirect](../../src/Plate.Web/Features/FeatureExceptionFilter.cs#L56)

### 6. Export all data omits user-created records

**Data export · High · Medium fix.** The ZIP file list has no saved meals or meal items, private food definitions, or per-exercise notes and bar overrides. The profile CSV also omits activity level. A food log cannot reconstruct the user's named meal or a machine's seat setting. Export these user-owned records and their relationships. Check the archive against a seeded account with a saved meal, a private food and an exercise note. The existing ZIP test pins the current file list. [Settings action](../../src/Plate.Web/Views/Settings/Index.cshtml#L84) · [Export contents](../../src/Plate.Web/Features/Export/DataExport.cs#L16) · [Export test](../../tests/Plate.Web.Tests/ExportTests.cs#L48)

## Quality of Life Improvements

### 7. An expired rest timer closes whichever sheet is open

**UX · Medium · Small fix.** I logged a set, opened Plates, and advanced the rest timer past its four-second completion grace period. Plates closed without a tap. Timer cleanup calls the shared sheet manager's unconditional hide operation. Give it a way to dismiss only the rest sheet. The timer bar can still clear while someone is reading plate weights or editing an exercise note. [Timer cleanup](../../src/Plate.Web/wwwroot/js/session.js#L593) · [Sheet manager](../../src/Plate.Web/wwwroot/js/motion.js#L342)

### 8. Adding several exercises can partially save a selection

**Logic · Medium · Medium fix.** Both pickers call a single-exercise service in a loop. Each call saves immediately. If the second selected custom exercise was archived in another tab, the first addition commits and the request then fails. Resubmitting the selection can add the first exercise again. Pass all selected IDs to a batch service, validate them together and commit once, preserving order and intentional duplicates. In a local template test I archived the second pick after opening the picker. The POST returned 404 and the first pick was present in the database. [Session picker](../../src/Plate.Web/Controllers/TrainController.cs#L94) · [Session save](../../src/Plate.Web/Features/Training/SessionService.cs#L250) · [Template picker](../../src/Plate.Web/Controllers/TemplatesController.cs#L103) · [Template save](../../src/Plate.Web/Features/Training/TemplateService.cs#L106)

### 9. The first historical target capture calculates the same trend repeatedly

**Performance · Medium · Small fix.** The backfill calls `TrendWeight.On` for each day. Each call rebuilds the entire series through that day. With 1,095 daily weigh-ins, the trend calculation alone allocated **105.2 MB** across those calls. Generating the series once allocated **0.21 MB** and produced the same sum of daily trends. This measures managed allocations for synthetic inputs, excluding EF and JSON. Generate the series once and advance through it and the ordered estimates as each snapshot is built. [Backfill loop](../../src/Plate.Web/Features/Food/NutritionTargets.cs#L148) · [Single-day lookup](../../src/Plate.Core/Nutrition/TrendWeight.cs#L48)

### 10. A four-week chart fetches and deserialises the entire snapshot history

**Performance · Medium · Small fix.** Progress asks for 28 days. It fetches every snapshot and maintenance estimate through today and decodes every snapshot's JSON before choosing the applicable values. This increases work on every visit as history grows. For each series, load the last record before the range plus changes within the range. The predecessor matters when the account had no visit during the first days displayed. [Chart queries](../../src/Plate.Web/Features/Body/BodyAnalytics.cs#L46)

### 11. Food matching keeps several index-aligned lists in one 91-line method

**Maintainability · Medium · Medium fix.** `ResolveAsync` maintains the parsed foods, best-ranked candidates, trace candidates, model shortlists, decisions and output rows in matching index order. It builds shortlists even for usual foods and private top matches, then clears them before asking the model. A change to filtering or ordering can associate a decision or diagnostic trace with the wrong food. Keep each item's candidates and trace together, construct model questions only for eligible items, and extract the per-item choice into a pure function. Preserve the full list of foods as model context and the existing trace contract. [FoodMatcher.ResolveAsync](../../src/Plate.Web/Features/Food/FoodMatcher.cs#L97)

### 12. Modal sheets allow keyboard focus to leave the dialog

**Accessibility · Medium · Medium fix.** The shared sheet manager focuses a control present when the sheet opens and handles Escape. It does not contain Tab navigation or make the background inert. Notes & bar opens with an empty sheet body, so even initial focus depends on whether the htmx response has arrived. Use native modal behaviour or explicitly contain focus, move it after async content arrives, and restore it to the opener on close. Add a keyboard check through the first and last controls. In Chromium, focusing the Add button in Quick add and pressing Tab twice put focus on the underlying page while the dialog was still open. [Sheet open](../../src/Plate.Web/wwwroot/js/motion.js#L324) · [Keyboard handler](../../src/Plate.Web/wwwroot/js/motion.js#L375) · [Exercise sheet](../../src/Plate.Web/Views/Shared/_ExerciseSheet.cshtml#L4)

### 13. Bulgarian labels need a language pass in two frequently used flows

**Localisation · Low · Small fix.** Under the protein gauge, the standalone caption reads **„протеин остават“**. **„оставащ протеин“** works under the number and unit already displayed. Three label-reader messages use Latin `g` or `ml` where the rest of the interface uses **г** and **мл**. The catalogue test checks only `kg` and `kcal`. Use the Bulgarian units in these messages and extend the test with bounded unit tokens. [Gauge layout](../../src/Plate.Web/Views/Today/Index.cshtml#L73) · [Gauge text](../../src/Plate.Web/Resources/strings.bg.json#L643) · [Label text](../../src/Plate.Web/Resources/strings.bg.json#L871) · [Unit check](../../tests/Plate.Web.Tests/BulgarianCatalogueTests.cs#L49)

### 14. Progress translates the names of custom exercises

**Localisation · Medium · Small fix.** I created a custom exercise named **Warm-up** in a Bulgarian account and logged a set. Progress displayed it as **Загрявка**. The picker passes every exercise name through the general string catalogue. The workout summary does the same for personal records and first-time lifts. Carry the exercise's ownership or its already-resolved display name through these projections and reuse the existing rule that preserves a user's spelling. [Progress picker](../../src/Plate.Web/Views/Progress/Index.cshtml#L110) · [Summary](../../src/Plate.Web/Views/Train/Summary.cshtml#L51) · [Exercise localisation rule](../../src/Plate.Web/Features/Localization/Localizer.cs#L87)

### 15. Two profile validation assertions duplicate stronger boundary tests

**Tests · Low · Small fix.** `Rejects_out_of_range_values` checks a height of 90 cm and protein of 5 g/kg. The dedicated theories already cover invalid **119 cm** and **3.6 g/kg**, plus the valid boundaries. Keep its distinctive unknown-time-zone assertion as a named case and remove the two redundant assertions. The benefit is clearer failure ownership. The full Core suite took a fraction of a second, so pruning calculation cases for speed has little payoff. [Mixed test](../../tests/Plate.Core.Tests/NutritionTests.cs#L204) · [Height boundaries](../../tests/Plate.Core.Tests/NutritionTests.cs#L231) · [Protein boundaries](../../tests/Plate.Core.Tests/NutritionTests.cs#L263)

## Quick Wins

| Change | Evidence to verify the fix |
|---|---|
| Reject a bare decimal separator in manual nutrition fields | Enter a single dot or comma, then a valid Bulgarian decimal such as 8,5. Only the valid number should save. |
| Feed per-day minimums into the protein chart | A historical 95 g minimum stays at 95 g when today's is 130 g. |
| Scope timer dismissal to the rest sheet | Let a timer finish while Plates is open. Plates stays visible. |
| Generate the backfill trend once | Compare all snapshot values on the same synthetic history and measure allocations. |
| Correct the Bulgarian caption and units | Read the gauge and the three label-reader messages in their rendered context. |

## Summary Statistics

| Primary category | High | Medium | Low |
|---|---:|---:|---:|
| Logic | 3 | 1 | 0 |
| UX | 2 | 1 | 0 |
| Data export | 1 | 0 | 0 |
| Performance | 0 | 2 | 0 |
| Maintainability | 0 | 1 | 0 |
| Accessibility | 0 | 1 | 0 |
| Localisation | 0 | 1 | 1 |
| Tests | 0 | 0 | 1 |
| **Total** | **6** | **7** | **2** |

Ten temporary diagnostic cases reproduced nine distinct behaviours in the local app or service. The reload case ran in both browser engines. The probes were removed from the worktree after verification.

The first full-suite attempt stopped during Web tests when its disposable PostgreSQL server entered recovery. A fresh run of `bash tools/check.sh` on the pinned revision passed all **1,419 existing checks** (55 JavaScript, 470 Core, 589 Web, 305 browser), with zero build warnings. The diagnostic cases counted above are separate from those totals.
