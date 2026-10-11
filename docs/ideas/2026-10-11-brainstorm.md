# Phase 16 and after: brainstorm, 2026-10-11

Brainstorm only; nothing here is planned yet. Sources: SPEC.md, the code at 3e19c70, the last 90 commits, Antoan's answer on who logs what, and these commands run on wizard: `feedback digest --since 60d`, `list --status open`, `parses --since 60d`, `labels --since 60d [--edited]`, `errors --since 30d`, `changelog list`, `show 2, 4, 5` and `ai show`. Further reads on wizard (`role list`, `admin audit`, `feedback label <id>`, an aggregate database query) were refused by the session's permission check, so there are no per-account counts and no fiber coverage from real logs.

## 1. Snapshot

- **Who logs what** (Antoan, not the data): the father rarely logs; friends log food mostly by barcode; Antoan types text. This matches the trace: all 11 text parses in 60 days are Antoan's, and the one friend in the feedback (Zdravko) reported a barcode problem (#5) and read a label.
- **The barcode path forgets you.** The barcode and search results fill the grams field with the food's serving or 100 g, even when the account has logged that product before (`Barcode.cshtml:40`, `Add.cshtml:126`); only Recent uses the last amount. After logging, the app goes back to the Food day, so a second product means Add, Scan and the camera again, and SPEC 7 notes that route changes can make iOS ask for the camera again. (Read from code; how often friends retype grams is inferred.)
- **Parsing is weakest on dishes.** 5 of 11 runs were never saved, all of them composite or restaurant dishes (fruit salad → canned fruit salad, bruschetta twice, "голям дюнер" → doner meat (#4), Hesburger mini burgers → a McDonald's cheeseburger). Single foods saved as suggested. That they went unsaved because of the match is inferred.
- **The same words give a different food each time:** "cottage cheese"/"извара" matched five different foods in five runs, and run 8 was changed by hand. SPEC 6.1 says matching uses "recent entries"; the code only boosts private foods.
- **Label reader:** 4 reads, 1 failed (not a table), 2 of 3 saved were edited.
- **Errors are not the problem:** two client view-transition aborts on 2026-10-05, no server errors in 30 days.
- **Training has had no attention since Phase 11** and the exercise library (2026-10-06); the last 90 commits are food entry, motion and test speed. No training feedback has ever been filed.
- **Stored but never shown:** `SetEntry.Rpe` (accepted, no input anywhere), session `Notes` (the placeholder asks "what to change next time"; the next session never shows it), `CompletedAt` beyond weekly volume, past `WeeklySummary` rows, `MaintenanceEstimate` history beyond 4 weeks, the foods chosen in `ParseRun.Saved`, AI cost (stdout logs only).
- **Fiber coverage, from seed data:** USDA 91%, Bulgarian rows 54%, Bulgarian generic dishes 0 of 332.
- **SPEC and code disagree:** SPEC 11 lists `feedback parses --corrected`; the CLI takes `--flagged` and ignores unknown flags. Changelog #9 still says "for both of you". Reports #4 and #5 carry version "unknown".

## 2. Ranked ideas

From a pool of 28. Ranked by impact divided by size (S = 1, M = 2, L = 3); ties go to higher impact, then lower risk. Small items that share a screen are bundled into one phase rather than ranked alone.

| # | Idea | One-line pitch | Evidence | Size | Impact | Risk | Spec sections |
|---|---|---|---|---|---|---|---|
| 1 | Log it again faster | The last amount on barcode and search, "Add and scan another", Recent ordered by meal, and a remembered food for typed words | Friends log by barcode; the grams prefill ignores history; logging returns to the day; "извара" → 5 foods in 5 runs | M | 4 | iOS may ask for the camera again on "scan another"; a one-off choice sticks | 4, 5.10 (new), 6.1, 7 Food |
| 2 | Starter plans, with last workout's note | A new account picks a 2-, 3- or 4-day plan; the next session shows the last one's note; Progress marks 10 to 20 weekly sets | Train and Today start empty; onboarding covers nutrition only; `Notes` is never shown again | M | 3 | Reads as a prescription; whether friends train with the app is unknown | 5.3, 5.11 (new), 7 Train, Today, Progress |
| 3 | Recipes | Build a home dish once from its ingredients and cooked weight, then log it by grams | All 5 unsaved parses were dishes; Bulgarian dishes lack fiber. Mainly helps the one account that types text | M | 3 | Scope creep (nesting, sharing); the cooked weight confuses people | 4, 5.12 (new), 6.1, 7 Food |
| 4 | Plateau check-in | After 4 sessions without an e1RM gain, offer a rep-range change or a swap | 5.1 only deloads after two failed sessions | M | 3 | False alarms on light weeks | 5.1, 5.2, 7 Train |
| 5 | Activity and AI cost in the digest | Per account: days active, entries by source, sets, AI calls and cost | Usage per account had to be asked; cost is only in logs; the "Later" cap needs it | S | 1 | Must stay counts, never contents (SPEC 12) | 11, 12 |
| 6 | Past weeks | A list of stored weekly summaries with that week's estimate and trend | Rows are stored; only the current week is shown | S | 1 | Low value | 6.5, 7 Progress |
| 7 | Portions per food | "1 slice", "1 medium" remembered per food | Run 10's gram corrections | M | 2 | Mostly covered by #1's last amount | 4, 6.1, 7 Food |
| 8 | Effort on the last set | An optional "reps left" chip used by progression | `Rpe` stored with no input | M | 2 | A tap against goal 1; changes 5.1 | 4, 5.1, 7 Train |

Rationale. #1 now serves every account that logs food: the friends through barcode and search, Antoan through text, at no AI cost. #2 matters for each friend who joins and wants to train, and carries two small training items that would otherwise wait indefinitely. #3 dropped from second place because it mostly helps one account. #5 would have answered the question I had to ask; build it before the "Later" AI cap.

## 3. Top 3 as draft spec sections

### 3.1 Log it again faster (section 7 Food, new 5.10, 6.1 addition)

Most food logged is food logged before. Friends scan the same products, and the grams sheet still offers the pack's serving every time; Antoan types the same words, and the ranking can pick a different food for them each time. The app should offer what this account did last time.

**Last amount.** The barcode result and each search result fill "How much?" with this account's most recent grams for that food in the last 90 days, else the serving, else 100 g, with "Last time" under the field when it came from history. Quick entries and 0 g are ignored. Read from `FoodLogEntry` (new index on `UserId, FoodItemId, CreatedAt`); nothing new is stored.

**Add and scan another.** The barcode result has "Add" and "Add and scan another". The second logs the entry, shows "Added {name}" and opens the scanner sheet again for the same day and meal. If the camera is refused or fails, the sheet's typed-digits field is focused instead. The Food day is shown when the sheet is closed.

**Recent by meal.** The Recent tab orders foods by how often this account logged them at the chosen meal in the last 30 days, then by overall count, then by the most recent; still 20, Quick entries still out.

**Usual food for typed words.** **UsualFood** (user-owned): `Id`, `UserId`, `Phrase` (up to 100 characters, unique per user), `FoodItemId` (cascade on delete), `Grams`, `LastUsedAt`. Confirming a parse writes one row per saved item that has a food, keyed by `NameOriginal`; the latest save wins. Before the ranking, an item's `NameOriginal` key, then its `NameEn` key, is looked up; a row whose food the account can still see is the match, High confidence, with the chip "Your usual" ("Обичайно"), and Jev gets that item as context only. When the parser assumed the amount, the row's grams replace it ("Your usual amount, {0} g"); a typed amount always wins. Rows unused for 180 days are removed; export includes them, `delete-user` removes them. This also does what SPEC 6.1 already says about recent entries.

No AI call anywhere here; everything works without a key.

**5.10 Core.** `LastAmount.Pick(entries, today)`; `RecentOrder.For(entries, meal, today)`; `UsualFoods.Key(text)` (lower case, trimmed, whitespace collapsed, leading and trailing amounts and units removed: 200г, 200 g, 2 бр., ½). Tests: the latest entry wins over a more frequent one; 0 g, Quick and 91-day-old entries are ignored; a food eaten 5 times at breakfast ranks first at Breakfast and below a dinner food at Dinner; "200г извара", "Извара" and "извара  " give one key; "200 g" alone gives none; a typed amount beats the usual grams. Web: another account's entries never set the last amount, and its food in a usual row is ignored (user isolation).

Open questions: whether "scan another" keeps the camera without a new prompt on iOS (test first on the iPhone; the typed-digits fallback covers a refusal); whether `NameEn` should also be written as a key.

**Phase 16: Log it again faster**
- The last amount, "Add and scan another", Recent by meal, `UsualFood`, and 5.10 in Core.
- **Done when:** the Core tests for 5.10 pass; on an iPhone at 390 px, in English and Bulgarian, a product logged at 250 g offers 250 g with "Last time" on its next scan and in search; three products are logged into one meal with "Add and scan another" without passing through the Food day; Recent at Breakfast lists breakfast foods first; "извара" proposes the food last confirmed for it, with "Your usual"; all of it works without an OpenRouter key; and the user isolation tests cover the last amount and usual foods.

### 3.2 Starter plans, with last workout's note (new 5.11, section 7)

A new account finishes the setup chat with nutrition targets and no workouts, and Today says "Make a template with the exercises you do". For a friend or a beginner, that is the hardest step in the app. A starter plan gives them a sensible first week; afterwards its templates are like any other.

**5.11 Plans** (`StarterPlans` in Core, data in code, exercises by seed name):
- Full body, 2 days (A/B): machines and dumbbells (leg press, machine chest press, seated cable row, lat pulldown, seated leg curl), 2 sets of 10 to 15, 90 s rest. Suggested at 65 and over.
- Full body, 3 days (A/B, alternating): 3 sets of 8 to 12. Suggested otherwise.
- Upper/Lower, 4 days: 3 sets of 6 to 10 on compound lifts, 10 to 15 on the rest.

Rules: each muscle twice a week (Schoenfeld, Ogborn & Krieger 2016); 10 or more weekly sets per muscle in the 3- and 4-day plans (Schoenfeld, Ogborn & Krieger 2017); 1 to 3 sets of 8 to 12 for novices (ACSM 2009); 2 to 3 days a week at moderate effort, machines first, for older adults (Fragala et al. 2019, NSCA; WHO 2020 says at least 2 days).

**Screens.** While an account has no unarchived templates, Train and Today's empty state show "Pick a starting plan" above "Create a template". The plan page shows each plan's days a week, estimated session length (sets × (rest + 45 s)) and exercises, marks one "Suggested for you", and ends "A starting point, not a prescription. Swap or remove anything." Choosing creates its templates, named in the account's language, and opens Train. An exercise with no history shows "First time: pick a weight you could lift 2 or 3 more times."

**Last workout's note.** A session started from a template shows the note of the last finished session of that template, if any, under the title: "Last time you wrote: …".

**Weekly sets.** The Progress volume chart shades 10 to 20 sets, with the footnote "Most growth comes from about 10 to 20 sets a week per muscle; fewer still works, especially when starting out."

No AI, no cost.

**Tests.** Every plan exercise resolves to a seed exercise (Web). Weekly sets through 5.3: chest, back, quads, hamstrings, glutes and shoulders at 6 to 20 in the 2-day plan and 10 to 20 in the others. `Recommend` at 64, 65 and with no birth year. English and Bulgarian names for every plan and template. Sessions estimated at 40 to 75 minutes. The note shown is the latest finished session's of the same template, never another account's.

Open question: whether to offer a home dumbbell plan.

**Phase 17: Starter plans**
- `StarterPlans` in Core, the plan page and empty-state links, the first-time hint, the last workout's note and the weekly sets band.
- **Done when:** the Core tests for 5.11 pass; an account invited at 390 px in Bulgarian picks the suggested plan, starts Full body A from Today and logs a set; an account aged 65 is offered the 2-day plan; the plan page has no sideways scroll at 320 px in either language; a note written on Upper A shows when the next Upper A starts; and the volume chart shows the band in both themes.

### 3.3 Recipes (section 4, new 5.12, section 7 Food)

Home-cooked dishes are the hardest thing to log by text: the parser guesses a generic dish, the database has few Bulgarian home dishes, and its 332 Bulgarian dishes carry no fiber. A recipe is built once from its ingredients and logged by weight like any food. Built from USDA ingredients, it carries fiber, which raises 5.8 coverage.

**Stored.** A recipe is a private `FoodItem` with `Source = Recipe` (never shared, no barcode), plus **Recipe** (`FoodItemId` PK, `YieldGrams` nullable, `UpdatedAt`) and **RecipeIngredient** (`Id`, `RecipeFoodItemId`, `SortOrder`, `Description`, `Grams`, `FoodItemId` nullable, and the per-100 g values used, snapshotted like `SavedMealItem`). Entries keep their stored totals, so editing a recipe changes only later entries.

**5.12 Recipe values** (`RecipeMath.Per100g`): totals are the sum of grams × per-100 g ÷ 100; the weight is `YieldGrams`, else the sum of the ingredient grams; per 100 g is totals × 100 ÷ weight, rounded as in 6.6. Fiber and saturated fat are known when the ingredients that have them carry at least 95% of the recipe's kcal, otherwise null. 1 to 30 ingredients of 1 to 3,000 g each; `YieldGrams` from 20% to 300% of the raw total.

**Screens.** A Recipes group under Saved meals on the Recent tab, with "New recipe": a name (up to 60 characters), ingredients typed as text through the parser and matcher (6.1) onto the confirmation list, or added from search, then "Weight when cooked (optional)" with the hint "Weigh the finished dish without the pot. Left empty, the raw weights are used." A summary shows kcal and protein per 100 g. After saving, the recipe is an ordinary food in search, Recent, the grams sheet and parse matching ("мусака 350г" finds it through the own-food bonus and usual foods). "Edit recipe" is in its grams sheet.

**Cost and limits.** A build makes the calls of one text parse and counts against the parse limit (30 per 10 minutes); estimated under $0.01, unmeasured because parser cost isn't stored. Without a key, ingredients come from search.

**Tests.** No yield uses the raw total; 70% concentrates; 250% dilutes; 19% and 301% are refused; fiber known at 95% kcal coverage and null at 94%; an estimated ingredient counts and is marked; zero ingredients are refused. Another account can't open or log a recipe; `delete-user` removes recipes.

**Phase 18: Recipes**
- The recipe entities and migration, `RecipeMath`, the recipe form and group, recipes in search, matching and Recent, export and deletion.
- **Done when:** the Core tests for 5.12 pass; on an iPhone at 390 px, in English and Bulgarian, a recipe typed as text with a cooked weight saves, "мусака 350г" parses to it and its entry shows fiber; editing it leaves yesterday's entry unchanged; it works without a key; and the user isolation tests cover recipes.

## 4. Not now

- **A lighter Today for accounts that rarely log food:** the father is the case, but nothing says what he opens the app for. Ask him before designing it.
- **Monthly AI cap per account (Later):** traced AI use is 11 parses and 4 label reads in 60 days, almost certainly under $0.10 in all, and the friends' barcode logging makes no AI calls. A cap needs stored cost first (#5).
- **Fruit and vegetables, sodium and sugars, diet quality score (Later):** not ready. Bulgarian dishes have no fiber, foods have no fruit or vegetable category, and real-log coverage is unmeasured.
- **Protein timing around training (Later):** with daily protein matched, timing effects largely disappear (Schoenfeld, Aragon & Krieger 2013), and per-meal protein already covers what matters.
- **Web Push rest timer while locked, or a meal estimated from a plate photo:** push needs VAPID keys, a library and a permission prompt while the wake lock already keeps the screen on; plate photos contradict 6.1's evidence that a model's energy estimates are off by about 35%.
