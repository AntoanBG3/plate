# Plate: Product and Technical Specification

Self-hosted training and nutrition tracker for me, my father and a few people I invite. Runs in Docker on the headless Ubuntu home server "wizard", used from iPhones as an installed PWA, reached remotely at `https://plate.peychev.uk` through the server's existing Cloudflare Tunnel. Replaces ad hoc calorie tracking with a system that logs workouts, suggests progression, parses food entries with an LLM, and estimates real maintenance calories from logged data.

Build in the phases at the end of this document. Each phase must be usable on its own before the next starts.

---

## 1. Goals and non-goals

**Goals**
- Logging a set at the gym takes one or two taps.
- The app tells me what weight and reps to aim for on every exercise.
- Food logging accepts plain text ("200g chicken breast, 150g cooked rice, a banana") and barcodes.
- Calorie targets adapt to my actual weight trend instead of a fixed guess.
- Fully separate accounts with no data leakage between them.

**Non-goals**
- Public sign-up, social features, sharing. Accounts come only from the CLI or a single-use invite from the developer (section 9).
- Native iOS app.
- Anonymous access. The app is reachable from the internet only through its Cloudflare Tunnel hostname, and every page except login requires an account (see section 9).
- Rest-timer alerts while the phone is locked (would need Web Push; revisit after Phase 5 if wanted).

---

## 2. Stack

| Concern | Choice |
|---|---|
| Runtime | .NET 10 (LTS), ASP.NET Core |
| UI | MVC with Razor views, htmx 2.0.x (pinned; not 4.x) for partial updates, Alpine.js only where client state is needed (rest timer, active set form). The offline set queue is a small plain-`fetch` module, not htmx (section 8). Page changes use a small soft-navigation module (`nav.js`): links and plain POST forms fetch the next page and swap the content inside a View Transition, falling back to a normal page load on any doubt |
| Charts | Chart.js |
| CSS | Tailwind via the standalone CLI (no Node build pipeline) or plain CSS; mobile-first, large touch targets |
| Front-end delivery | No CDNs. Pinned copies of every library (htmx, Alpine, Chart.js, the barcode scanner and its `.wasm`) are vendored into `wwwroot/lib` and precached by the service worker |
| Database | PostgreSQL 18, EF Core with Npgsql provider |
| Auth | ASP.NET Core Identity, cookie auth, sliding 30-day persistent login, lockout and login rate limiting on (section 9). No Cloudflare Access in front of the app, except the admin area (section 12), whose Access token is checked with `Microsoft.IdentityModel.JsonWebTokens`. Passkeys (WebAuthn, built into Identity in .NET 10) alongside passwords |
| LLM | OpenRouter chat completions API (`openai/gpt-6-luna` by default, model picked in the admin area) via typed HttpClient, behind an interface. It extracts foods and grams; nutrition values come from the food database (section 6.1). A separate vision model (`google/gemini-3.5-flash-lite` by default) reads nutrition labels from photos (section 6.6) |
| Food matching | Optionally a decision model (TypeSafe's `typesafe/jev-1.13` is the one evaluated) through OpenRouter's Decisions API, chosen in the admin area from OpenRouter's live list: it picks which shortlisted database food each parsed item is, or that none fits (section 6.1) |
| Food database | Users' own and shared `FoodItem`s, a locally imported subset of USDA FoodData Central for generic foods, and Open Food Facts API v3 for barcodes |
| Barcode scanning | `barcode-detector` ponyfill (ZXing-C++ compiled to WASM), self-hosted. Safari on iOS ships the native `BarcodeDetector` API disabled, and `@zxing/browser` sits on a library in maintenance mode |
| Tests | xUnit, FluentAssertions; Testcontainers (`postgres:18`) for PostgreSQL integration tests |
| Deployment | docker-compose on wizard; HTTPS via the existing Cloudflare Tunnel (`plate.peychev.uk`) |
| Runtime image | `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra` (includes ICU and time zone data for `Europe/Sofia` and Bulgarian text). No Native AOT (unsupported for MVC and cookie auth) |

Server-rendered MVC with htmx is chosen over Blazor Server deliberately: gym signal drops would kill Blazor Server circuits mid-workout, while plain HTTP requests can simply be retried.

.NET 10 is LTS, supported until 2028-11-14. PostgreSQL 18 is supported until 2030-11-14.

---

## 3. Solution structure

```
Plate.sln
src/
  Plate.Core/            Domain entities, enums, pure calculation services. No EF, no HTTP, no ASP.NET references.
  Plate.Web/             Controllers, views, view models, htmx partials, static assets, PWA manifest and service worker.
    Data/                     EF DbContext, migrations, seed data (exercises, USDA subset).
    Integrations/             OpenRouter client and model catalog, Jev match decider, label reader, Open Food Facts client.
    Features/                 Thin feature services that use the DbContext directly and call Core for the math.
tests/
  Plate.Core.Tests/      Unit tests for all calculation logic.
  Plate.Web.Tests/       Integration tests against real PostgreSQL via Testcontainers, including user-isolation tests.
docker/
  Dockerfile
  docker-compose.yml
  docker-compose.dev.yml
```

All business math (progression, e1RM, volume, trend weight, maintenance estimate, targets) lives in `Plate.Core` as pure, deterministic functions that take data in and return results. Controllers never contain calculations.

There is no separate Infrastructure project and no generic repository layer: the DbContext, with its global query filters, is injected into feature services. The `Core` boundary is what keeps the math testable; a third project adds wiring without adding safety at this size.

---

## 4. Data model

All entities owned by a user carry `UserId` and every query filters by the current user. Enforce this with an EF global query filter on user-owned entities in addition to explicit checks.

Units: weights in kilograms (`decimal(6,2)`), energy in kcal, macros in grams. Dates that represent a "day" are `DateOnly` computed in the user's time zone (default `Europe/Sofia`), never UTC midnight. Timestamps are `DateTimeOffset` stored in UTC.

### Users and profile

**AppUser** (Identity user)

**UserProfile**
- `UserId` (PK, FK)
- `DisplayName`
- `TimeZoneId` (default `Europe/Sofia`)
- `HeightCm`
- `Sex` (Male, Female; also sets the calorie floor in 5.6), `BirthYear`, `ActivityLevel` (Inactive, LowActive, Active, VeryActive; nullable, null counts as Inactive): used for the initial maintenance guess (5.5)
- `InitialMaintenanceKcal` (nullable; if null, the NASEM 2023 estimate in 5.5 is used)
- `OnboardedAt` (nullable; set when the user finishes or skips the setup chat, section 6.4)
- `Goal` (Cut, Maintain, Bulk)
- `TargetRatePercentPerWeek` (decimal, e.g. 0.5 means 0.5% of bodyweight per week; ignored for Maintain)
- `ProteinGramsPerKg` (nullable; null is Auto, the default by goal in 5.6)
- `WeeklySummaryEnabled` (default true; 6.5)
- `CutStartedOn` (nullable `DateOnly`; set when the goal changes to Cut and after a diet break, cleared when it changes away; 5.9), `DietBreakUntil` (nullable `DateOnly`, the last day of a diet break), `CutCheckDismissedUntil` (nullable `DateOnly`; check-ins are hidden through this day)
- `DefaultWeightIncrementKg` (default 2.5)
- `BarWeightKg` (default 20) and `PlatesKg` (the plates the gym has; default 25, 20, 15, 10, 5, 2.5, 1.25), used by the plate calculator

**Invite** (not user-owned, no user filter; section 9)
- `Id`, `TokenHash` (SHA-256 of the token, unique; the token itself is never stored)
- `Note` (up to 100 characters, e.g. "Ivan from the gym"), `CreatedByUserId`, `CreatedAt`, `ExpiresAt`
- `RedeemedAt`, `RedeemedByUserId` (nullable, set null when that account is deleted), `RevokedAt`

### Training

**Exercise**
- `Id`, `Name`, `PrimaryMuscle` (enum), `SecondaryMuscles` (list of enum, stored as an array column), `Equipment` (Barbell, Dumbbell, Machine, Cable, Bodyweight, Other)
- `OwnerUserId` (nullable; null means shared seed library, non-null means a user's custom exercise)
- `IsArchived`

Muscle enum: Chest, Back, Shoulders, Biceps, Triceps, Forearms, Quads, Hamstrings, Glutes, Calves, Abs.

Seed about 90 common exercises, each with a Bulgarian name in the gym vocabulary Bulgarian lifters use (bench press, incline dumbbell press, squat, Romanian deadlift, deadlift, pull-up, lat pulldown, barbell row, cable row, overhead press, lateral raise, leg press, leg curl, leg extension, calf raise, curls, triceps pushdown, etc.).

**UserExerciseSettings**
- `UserId`, `ExerciseId` (composite key)
- `Note` (up to 200 characters, e.g. "seat 4, neutral grip"), `BarWeightKg` (nullable; overrides the profile's bar, and turns the plate calculator on for non-barbell exercises)
- Exercises are shared seed rows, so per-user notes live here.

**WorkoutTemplate**
- `Id`, `UserId`, `Name` (e.g. "Upper A"), `SortOrder`, `IsArchived`

**TemplateExercise**
- `Id`, `TemplateId`, `ExerciseId`, `SortOrder`
- `WorkingSets` (int), `RepMin`, `RepMax`
- `WeightIncrementKg` (nullable; falls back to profile default)
- `RestSeconds` (default 120)

**WorkoutSession**
- `Id`, `UserId`, `TemplateId` (nullable for freestyle sessions)
- `StartedAt`, `EndedAt` (nullable while active), `Notes`
- Only one active session per user at a time.

**SessionExercise**
- `Id`, `SessionId`, `ExerciseId`, `SortOrder`
- Snapshot of `RepMin`, `RepMax`, `WorkingSets`, `WeightIncrementKg` at session start, so editing a template later does not rewrite history.

**SetEntry**
- `Id`, `SessionExerciseId`, `SetNumber`
- `WeightKg`, `Reps`, `Rpe` (nullable, 5 to 10 in 0.5 steps)
- `IsWarmup` (warmups are excluded from progression, volume and e1RM)
- `CompletedAt` (sent by the client, so a set retried later keeps the time it was actually done)
- `ClientId` (Guid generated in the browser, unique index) so retried requests are idempotent.

**Soft delete.** `SetEntry`, `SessionExercise`, `TemplateExercise` and `FoodLogEntry` have `DeletedAt` (nullable). Deleting sets it; named EF query filters (`Owner`, `SoftDelete`) hide deleted rows everywhere, and a removed session exercise hides its sets too. Undo clears it within 10 minutes; an hourly job removes rows deleted more than 24 hours ago.

### Body

**BodyweightEntry**
- `Id`, `UserId`, `Date` (unique per user and date; a second entry the same day overwrites), `WeightKg`

### Nutrition

**FoodItem** (reusable food definitions and cache)
- `Id`, `OwnerUserId` (nullable for shared items: the Open Food Facts cache, the USDA subset, and manual items shared for a barcode)
- `AddedByUserId` (nullable, no foreign key, like `Invite.CreatedByUserId`): who created a manual item. Kept when that account is deleted; null only when the creator is unknown. Used only to tell a user that a shared item came from someone else; never shown as a name.
- `Name`, `NameBg` (nullable, Bulgarian name), `Brand` (nullable), `Barcode` (nullable). A barcode is unique among shared items, and unique per owner among private ones, so a private item doesn't block anyone else's.
- `KcalPer100g`, `ProteinPer100g`, `CarbsPer100g`, `FatPer100g`
- `FiberPer100g`, `SaturatedFatPer100g` (nullable; null means unknown, not zero)
- `DefaultServingGrams` (nullable)
- `PieceGrams` (nullable): one piece's weight, for a counted amount (6.1 step 4). A serving can be several pieces ("2 biscuits (30 g)"), so `DefaultServingGrams` never stands in for it.
- `PackGrams` (nullable): the whole pack's net weight in grams, for a fraction of a pack.
- `ValuesEstimated`: the per-100 g values are an estimate from a recipe, not a printed label or a composition table; the confirmation row says so.
- `Unit` (Grams, Millilitres; Grams for every food from before it): a food entered from a per-100 ml label (6.6), or with "Per 100 ml" picked on the manual form, keeps that basis. Its per-100 values, serving and every amount logged of it are then in ml, shown with "ml" wherever an amount is entered or shown; nothing converts between ml and grams. Open Food Facts products and the seeded foods are in grams.
- `Source` (OpenFoodFacts, Usda, Manual, Ai)
- `Unnamed`: Open Food Facts had no name for the product, so `Name` stands in from its brand and quantity (6.2) until a user names it.
- A manual item with a barcode is shared (`OwnerUserId` null) when the user leaves "Share with other users" ticked (the default), so others who scan the same packaged product don't re-enter it; Open Food Facts coverage of Bulgarian products is thin, so this path will be common. Unticked, it is private like a manual item without a barcode. Sharing can't be changed after saving.
- Barcode lookup checks the user's own items first, then shared items, then Open Food Facts.
- Existing shared manual items get `AddedByUserId` once, from the user of the earliest entry that logged them; with no entry it stays null, and the item shows no mark.
- Rolling this change back clears the barcode of private items whose barcode another item also has, so the older one-barcode-per-food index can return; shared items keep theirs.

**LabelScan** (user-owned, section 6.6)
- `Id`, `UserId`, `CreatedAt`, `Model`, `LatencyMs`, `Error` (nullable)
- `Reading` (jsonb, the model's answer as returned), `Values` (jsonb, the per-100 g values and serving after 6.6's normalizing, with its notes)
- `FoodItemId` (nullable, set null on delete), `Saved` (jsonb, the values the user saved, null until saved), `Edited` (bool: any of energy, protein, carbs, fat, fiber, saturated fat or serving differs from `Values` beyond rounding, or the unit was changed). `Values` keeps the unit under `unit`; a scan stored without it was read as per 100 g.
- **LabelScanPhoto**: one to three photos per scan, in the order taken (`Position` 0 to 2, unique per scan; `ContentType`, `Width`, `Height`, `Bytes`), in their own table so lists never load images. Deleted 30 days after the scan; the scan row is kept 180 days, like a ParseRun.

**Recipe** (section 5.12; owned through its food)
- `FoodItemId` (key and foreign key, cascade): the recipe is a private `FoodItem` with `Source` Recipe, never shared and without a barcode, whose per-100 g values are the dish's. Its owner owns the recipe; the query filters go through the food.
- `YieldGrams` (nullable): the finished dish's weight; null uses the ingredients' raw weight. `UpdatedAt`.

**RecipeIngredient**
- `Id`, `RecipeFoodItemId` (cascade), `SortOrder`, `Description`, `Grams`, `FoodItemId` (nullable, set null on delete; null for a model's estimate), and the per-100 g values used, snapshotted like `SavedMealItem`. An ingredient that is a database food takes that food's values when saved, never the posted ones. A recipe can't be an ingredient.
- Entries logged from a recipe keep their stored totals, so editing a recipe changes only later entries; deleting one keeps its entries, unlinked.

**SavedMeal**
- `Id`, `UserId`, `Name` (up to 60 characters), `CreatedAt`

**SavedMealItem**
- `Id`, `SavedMealId`, `SortOrder`, `Description`, `Grams` (0 for a quick entry), `Unit` (the entry's), stored `Kcal`, `ProteinG`, `CarbsG`, `FatG`, `FiberG` and `SaturatedFatG` (both nullable), `FoodItemId` (nullable), `Source`
- A snapshot of a logged entry, so later food edits don't change the saved meal.

**FoodLogEntry**
- `Id`, `UserId`, `Date`, `Meal` (Breakfast, Lunch, Dinner, Snack)
- `Description` (what is shown in the log, e.g. "Chicken breast, grilled")
- `Grams`: the amount, in `Unit`
- `Unit`: the food's unit when logged (grams for a food without one and for quick entries), kept so the day shows what it showed then
- `Kcal`, `ProteinG`, `CarbsG`, `FatG` (stored totals, so the entry is stable even if the FoodItem changes)
- `FiberG`, `SaturatedFatG` (nullable stored totals; null when the food has no value)
- `FoodItemId` (nullable), `Source` (Barcode, Search, Ai, Manual, Recent, Quick). A Quick entry is calories and macros typed directly: no food, `Grams` 0, edited by its numbers and never scaled.
- `AiModel` (nullable; the OpenRouter parser model id for AI-parsed entries, so a shift in estimates after a model change can be traced; an estimate's values come from the estimator model recorded on its parse run, 6.1 step 6)
- `MatchModel` (nullable; the decision model version that chose the matched food or decided there is none, for the same reason; null when the database ranking decided)
- `Status` (nullable, up to 30 characters; how the food is, shown beside its name: "raw", "boiled", "dried", in the language it was logged in. Set for parsed entries and kept when a meal is copied; other entries show raw or cooked read from the linked food's name)
- `CreatedAt`

**FoodLogRequest** (user-owned, section 8)
- Composite key `UserId`, `RequestId` (Guid), `CreatedAt`, `Location` (the first successful redirect).
- A receipt committed atomically with a plain-POST food addition. Retained so an old form cannot add the same entries again, included in data export, and deleted with the account.

**UsualFood** (user-owned, section 6.1)
- `Id`, `UserId`, `Phrase` (up to 100 characters, the 5.10 key of a food's name as the user typed it; unique per user), `FoodItemId` (cascade on delete), `LastUsedAt`
- Which food this account means by those words. Written when a parse is confirmed, read before the ranking. Removed after 180 days unused (section 11), included in the data export, deleted with the account.

**MaintenanceEstimate**
- `Id`, `UserId`, `CalculatedOn` (DateOnly)
- `EstimatedTdeeKcal`, `TargetKcal`, `TargetProteinG`
- `TrendWeightKg`, `DaysOfData`, `LoggingCompleteness` (0 to 1), `Method` (Initial, Adaptive)

**WeeklySummary** (6.5)
- `Id`, `UserId`, `WeekStart` (the Monday of the week it describes; unique per user and week)
- `Summary` (up to 400 characters), `Tips` (up to three, each up to 160), `Motivation` (up to 160)
- `Language` (the account's language when written), `Model`, `CreatedAt`

**NutritionTargetDay**
- Composite key `UserId`, `Date` (the user's local day), with an owner query filter and account-delete cascade.
- `Values` (jsonb): the computed calorie and protein targets, macro ranges, floor, trend weight and maintenance actually shown that day. Today's snapshot may change, and is written only when it does; past snapshots are not rewritten. Days without a snapshot carry the previous snapshot forward. The first time a user's targets are captured (before any profile change), every day from their first estimate to yesterday is captured as it was shown before snapshots existed: the stored estimate's calorie and protein targets, with the other macro ranges fitted to those two numbers (the protein aim stays the recorded one; the minimum never exceeds it). The JSON property names are a fixed format, not the Core record names.

---

## 5. Core calculations (Plate.Core)

Each of these is a pure service with its own unit test class. Tests must cover the edge cases listed.

### 5.1 Progression (double progression)

Input: the exercise's rep range and increment, plus working sets from the last two sessions containing this exercise for this user.
Output: suggested weight and target reps per working set, and a reason string shown in the UI ("All sets hit 12, +2.5 kg").

Rules, applied in order:
1. **No history:** no suggestion; show empty fields.
2. **Deload:** if in each of the last two sessions any working set fell below `RepMin` at the same weight, suggest weight times 0.9, rounded down to the increment, reps at `RepMin`.
3. **Progress:** if every working set in the last session reached `RepMax`, suggest last weight plus increment, reps at `RepMin`.
4. **Hold:** otherwise keep the same weight, target reps for each set equal to that set's last reps plus 1, capped at `RepMax`.

When sets in the last session used different weights, use the weight of the heaviest working set and only count sets at that weight. Round all suggested weights to the nearest multiple of the increment. Bodyweight exercises progress reps only (weight stays 0 unless the user logged added weight).

Edge cases to test: no history, one prior session only (deload cannot trigger), mixed weights, increment of 1.25 and 5, partial sets logged, warmups present.

### 5.2 Estimated 1RM

Epley: `e1RM = weight * (1 + reps / 30)`. Only calculate for working sets with reps between 1 and 12; a single rep returns the weight itself. Per session, an exercise's e1RM is the best set's value.

### 5.3 Weekly volume

Working sets per muscle per ISO week. A set counts 1.0 toward the exercise's primary muscle and 0.5 toward each secondary muscle.

### 5.4 Trend weight

Exponential moving average with smoothing factor 0.1, seeded with the first weigh-in. For days with no weigh-in, carry the previous trend value forward unchanged (do not interpolate). Return a series of `(Date, RawWeight?, TrendWeight)`.

### 5.5 Adaptive maintenance estimate

Checked when the Today page loads. The estimate is updated **once per week**: if the latest stored `MaintenanceEstimate` for the user is less than 7 days old, it is reused as is; otherwise a new one is calculated and stored. (A daily update with the weekly clamp and 0.7/0.3 smoothing below would compound seven times a week, so the cadence is fixed at weekly.)

1. **Prior:** `InitialMaintenanceKcal` if set, else the NASEM 2023 estimated energy requirement for adults (Dietary Reference Intakes for Energy, 2023) using the latest trend weight, height, age (`currentYear - BirthYear`) and activity level. These equations were fitted to total energy expenditure measured with doubly labeled water, one equation per sex and activity category, so there is no separate BMR or multiplier. Age in years, height in cm, weight in kg:

   | | Men | Women |
   |---|---|---|
   | Inactive (PAL 1.00–1.53) | 753.07 − 10.83·age + 6.50·h + 14.10·w | 584.90 − 7.01·age + 5.72·h + 11.71·w |
   | Low active (1.53–1.68) | 581.47 − 10.83·age + 8.30·h + 14.94·w | 575.77 − 7.01·age + 6.60·h + 12.14·w |
   | Active (1.68–1.85) | 1004.82 − 10.83·age + 6.52·h + 15.91·w | 710.25 − 7.01·age + 6.54·h + 12.34·w |
   | Very active (1.85–2.50) | −517.88 − 10.83·age + 15.61·h + 19.11·w | 511.83 − 7.01·age + 9.07·h + 12.56·w |

   The equations can cross for very small bodies (a very active man of 150 cm and 45 kg computes below an active one), so the result is the highest value among the chosen category and every lower one. A null activity level counts as Inactive. The adult equations are published for 19 and over and are used from 18 (the minimum age, 5.7). Round to the nearest kcal. Test: a 30-year-old man of 180 cm and 80 kg gives 2,726 / 2,946 / 3,126 / 3,496 kcal; a 30-year-old woman of 165 cm and 60 kg gives 2,021 / 2,183 / 2,319 / 2,552.

   Why this equation: it predicts total expenditure directly rather than resting rate times a guessed factor, and BMR-times-multiplier calculators overestimate for most gym-goers with desk jobs. All equations still miss individuals by more than 10% about half the time (a 2024 doubly labeled water comparison), which is why the adaptive estimate below replaces the prior as soon as there is data. Lean-mass equations (Katch-McArdle, Cunningham) were no more accurate in a 2023 meta-analysis of athletes and need a body fat figure that home scales measure poorly, so body fat is not asked.
2. **Window:** the most recent 21 days ending yesterday (today's log is incomplete).
3. **Data requirements:** at least 14 days in the window (the window starts no earlier than the first weigh-in, since the trend begins there), food logged on at least 80% of days, and weigh-ins on at least 50% of days. A day with under 200 kcal logged counts as not logged. If not met, carry forward the last `Adaptive` estimate (or the prior if there is none) and mark `Method = Initial`.
4. **Calculation:**
   - `avgIntake` = mean daily kcal over days that have food logged. Unlogged days are assumed to equal this average.
   - `deltaTrend` = trend weight at window end minus trend weight at window start.
   - `rawTdee = avgIntake - (deltaTrend * 7700 / (windowDays - 1))`. The trend moves over `windowDays - 1` days between the first and last day of the window; dividing by `windowDays` would settle about 5% short (2,524 instead of 2,550 in the loss test below).
5. **Smoothing:** `newTdee = 0.7 * previousEstimate + 0.3 * rawTdee`, then clamp so it moves at most 100 kcal from the previous estimate (one weekly update). Store `Method = Adaptive`.
6. **Confidence note:** if the window contains a gap of 3 or more consecutive days without a weigh-in, show "low confidence" next to the estimate on the Today page. The calculation is unchanged.

Test with synthetic data: stable weight at known intake returns that intake; steady loss of 0.5 kg per week at 2,000 kcal converges near 2,550 over successive weekly updates; missing logging days below the threshold carries the last estimate forward; days under 200 kcal count as unlogged; noisy daily weights do not cause jumps beyond the clamp; a second call within 7 days returns the stored estimate.

### 5.6 Daily targets

- **Maintain:** `TargetKcal = TDEE`.
- **Cut / Bulk:** daily adjustment = `TrendWeight * TargetRatePercentPerWeek / 100 * 7700 / 7`, subtracted for Cut, added for Bulk.
- **Safety limits:** `TargetRatePercentPerWeek` is capped at 1.0 for Cut and 0.5 for Bulk in profile validation. `TargetKcal` is never below 1,800 for men or 1,500 for women; if the limit applies, show a short note on the Today page.
- **Diet break:** while today is on or before `DietBreakUntil`, a Cut uses `TargetKcal = TDEE` and keeps its protein aim (5.9).

**Macro targets.** Computed from `TargetKcal` (after the floor), trend weight, height, age and goal. Without a trend weight there are no macro targets, as for protein today. Targets are a minimum and an aim, or a range, never one exact number, because the evidence behind them is a range.

- **Reference weight:** `refKg = TrendWeight`, or `27 * heightM²` when height is known and BMI is over 27. Grams per kg of total weight overshoots for people carrying a lot of fat (WHO bases the safe protein level for overweight people on an acceptable weight; Adv Nutr 2021). BMI 27 rather than 25, so muscular lifters keep their full weight. Example: 110 kg at 175 cm gives 82.7 kg.
- **Protein minimum:** `1.6 * refKg` under 65, `1.2 * refKg` at 65 and over (no birth year counts as under 65). 1.6 g/kg is where added protein stopped adding lean mass in Morton et al. 2018 (upper CI 2.2) and where Nunes et al. 2022 found gains under 65; at 65 and over, Nunes found gains from 1.2 to 1.59, matching PROT-AGE's 1.2 to 1.5 for active older adults.
- **Protein aim:** `ProteinGramsPerKg * refKg` when set; Auto uses 1.8 for Maintain and Bulk, 2.2 for Cut, and 2.4 for a Cut faster than 0.75% a week. Helms et al. 2014 found 2.3 to 3.1 g/kg of lean mass in a deficit, more for bigger deficits and leaner people; ISSN 2017 calls 1.4 to 2.0 a minimum and says more helps in a deficit. The aim is never below the minimum.
- **Fat:** floor = the higher of `0.20 * TargetKcal / 9` and `0.5 * refKg`; ceiling = `0.35 * TargetKcal / 9`. Low-fat diets lowered testosterone in men (Whittaker 2021, SMD −0.38); ACSM and Iraki et al. 2019 give 20 to 35% of energy, about 0.5 to 1.5 g/kg.
- **Carbs:** the rest, as a range from `(TargetKcal − 4 * proteinAim − 9 * fatCeiling) / 4` to `(TargetKcal − 4 * proteinAim − 9 * fatFloor) / 4`, each clamped at 0. If the top of the range would be negative (a high custom g/kg on a calorie floor), the protein aim is lowered, in 5 g steps but not below the minimum, until the top is 0 or more, and the targets carry the note "Protein and fat fill your whole calorie target."
- **Fiber minimum:** the higher of 25 g and `14 * TargetKcal / 1000` (IOM 14 g per 1,000 kcal; WHO 2023 at least 25 g).
- **Saturated fat maximum:** `0.10 * TargetKcal / 9` (WHO 2023, Dietary Guidelines).
- **Per-meal protein** for Breakfast, Lunch and Dinner (not Snack): `0.3 * refKg` under 65, and the higher of `0.4 * refKg` and 25 g at 65 and over. The per-meal protein synthesis breakpoint was 0.24 g/kg in younger and 0.40 g/kg in older people (Morton 2018), rounded up here for mixed meals; older adults need 25 to 30 g a meal (Nowson & O'Connell 2015).
- **Rounding:** protein, fat, carbs and per-meal protein to the nearest 5 g, fiber and saturated fat to the whole gram. Carbs are computed from the rounded protein aim and fat values.

Test: a 30-year-old man of 180 cm and 80 kg cutting 0.5% a week at a TDEE of 2,500 (target 2,060) gets protein minimum 130 and aim 175, fat 45 to 80, carbs 160 to 240, fiber 29, saturated fat 23 or less, 25 g per meal; on Maintain (2,500) protein 130 and 145, fat 55 to 95, fiber 35, saturated fat 28. 110 kg at 175 cm cutting 0.5% gets a protein aim of 180, not 240. A 70-year-old at 80 kg gets a minimum of 95 and 30 g per meal. A Cut at 1.0% a week aims at 2.4 g/kg. BMI exactly 27 uses the trend weight. No height uses the trend weight. A custom 3.5 g/kg on a 1,500 kcal floor lowers the aim and sets the note.

### 5.7 Profile limits

One set of limits in Core (`ProfileLimits`), used by profile validation, the weigh-in, the Settings inputs, the setup chat's draft and its prompt, so a value one screen accepts is never refused by another.

| Field | Limit |
|---|---|
| `DisplayName` | 1 to 50 characters after trimming |
| `BirthYear` | age 18 to 100 in the current year (the energy equations, the calorie floors and the rate caps are adult guidance) |
| `HeightCm` | 120 to 230 |
| Bodyweight | 30 to 300 kg |
| `InitialMaintenanceKcal` | 1,200 to 6,000 |
| `Sex`, `Goal`, `ActivityLevel` | a defined value (a posted number outside the enum is refused) |
| `TargetRatePercentPerWeek` | 0 to 1.0 for Cut, 0 to 0.5 for Bulk (5.6) |
| `ProteinGramsPerKg` | empty (Auto) or 0.8 to 3.5; at 65 and over, a value under 1.2 shows a warning but saves |
| `DefaultWeightIncrementKg` | 0.25 to 20 |
| `BarWeightKg` | 0 to 50 |
| `PlatesKg` | from the plate list |

Tests cover each limit just inside and just outside.

### 5.8 Weekly nutrition

Daily targets are judged over the week, not the day. For the 7 days ending yesterday, over days with food logged (200 kcal or more, as in 5.5): average kcal, protein, fat, carbs, fiber and saturated fat, the number of days protein reached its minimum, and the number of days fat stayed at or above its floor, each against today's targets. With fewer than 3 logged days, show nothing.

Fiber and saturated fat are not known for every food. A day's **coverage** is the share of its kcal from entries that have the value. Under 80%, the day's fiber is shown as "at least" and its saturated fat as incomplete, because a missing value would otherwise read as zero and always pass a maximum. The weekly averages of these two use only days at 80% or more, and say so.

Test: unlogged and under-200 kcal days are skipped; averages over logged days only; coverage just under and at 80%; fewer than 3 logged days returns nothing.

### 5.9 Cut check-ins

Only when the goal is Cut, shown on Today, each hidden for a week with "Dismiss" (`CutCheckDismissedUntil`):

- **Too fast:** trend weight fell more than 1% of bodyweight a week over the last 14 days, and more than 1.5 times the target rate. "You're losing faster than planned. Fast cuts cost more muscle; your target will adjust, or lower the rate in Settings." Helms et al. 2014 recommend 0.5 to 1% a week to keep lean mass. The adaptive estimate already corrects calories, so this only informs.
- **Protein low:** the 5.8 weekly average protein is under the minimum. "Protein averaged {0} g this week, under your minimum of {1} g."
- **Diet break:** 12 weeks or more after `CutStartedOn`, offer "Take a 2-week break at maintenance". Accepting sets `DietBreakUntil` to today plus 13 days. During the break the target is maintenance (5.6) and Today shows "Diet break until {date}"; the day after it ends, `CutStartedOn` is set to that day, so the next offer comes 12 weeks later. "End break" ends it early the same way. Evidence is modest: in MATADOR (Byrne 2018, men with obesity) two weeks on and two off lost more fat than a continuous diet, and in ICECAP (Campbell 2020, 27 resistance-trained adults over 7 weeks) two refeed days a week kept more fat-free mass. So the break is offered, never imposed.

Test: the rate check at 1% and just over, and at 1.5 times the target; protein average just under and at the minimum; the break offer at 83 and 84 days; target kcal during and after a break; the reset of `CutStartedOn`; a dismissal hides check-ins through its day only.

### 5.10 Repeat logging

Most food logged is food the same account has logged before: friends scan the same products, and the same words come back in typed entries. Amounts and choices therefore come from the account's own history.

- **Last amount** (`LastAmount.Pick`): for one food, the grams of the account's most recent entry for it in the last 90 days, by `CreatedAt`. Entries of 0 g (Quick) don't count. The most recent wins over the most frequent, since an amount changes when a pack size or a habit does. No entry means no last amount.
- **Recent order** (`RecentOrder.For`): the Recent tab's foods (the last 30 days, grouped by food and name as before) ordered by the number of times logged at the chosen meal, then the number of times in all, then the latest entry, then the name. The first 20 are shown.
- **Usual food key** (`UsualFoods.Key`): lower case; trimmed; whitespace collapsed to one space; punctuation removed except `%` and characters inside a word; then leading and trailing tokens that are an amount are removed: a number, a fraction (½, 1/2), or a number joined to or followed by a unit (g, гр, г, kg, кг, ml, мл, l, л, бр, бр.). An empty result is no key.

Test: the latest entry wins over a more frequent older one; entries of 0 g and entries 91 days old are ignored and 90 days old are kept; a food logged 5 times at Breakfast and once at Dinner ranks first at Breakfast and below a food logged 3 times at Dinner at Dinner; ties fall to the total, then the latest entry; "200г извара", "Извара" and "извара  " give one key; "2 бр. банани" gives "банани"; "½ avocado" and "1/2 avocado" give "avocado"; "мляко 2%" and "7days croissant" are kept whole; "200 g" gives no key.

### 5.12 Recipes

A home dish built once from its ingredients and logged by weight like any food (`RecipeMath` in Core).
- Totals are the sum of grams × per-100 g ÷ 100. The dish's weight is `YieldGrams` when given, otherwise the sum of the ingredients' grams. Per 100 g is totals × 100 ÷ that weight, kcal rounded to a whole number and the rest to 0.1.
- Fiber and saturated fat are known when the ingredients that have them carry at least 95% of the recipe's kcal (all of them when it has none); they then add up only the ingredients that have them. Otherwise they are unknown.
- 1 to 30 ingredients of 1 to 3,000 g; a name of 1 to 60 characters, unique among the account's recipes (ignoring case); `YieldGrams` from 20% to 300% of the raw total, and never one that makes the values impossible per 100 g (`FoodMath.Validate`). All of this is checked again on every save.
- An ingredient in ml (a food from a per-100 ml label) counts 1 ml as 1 g toward the raw weight, and its row in the editor shows g; a recipe is always per 100 g.

Test: no cooked weight uses the raw total; 70% concentrates; 250% dilutes; 20% and 300% are accepted and 19.8% and 301% refused; fiber known at 95% of kcal and unknown below; known fiber adds up only the ingredients that have it; zero and 31 ingredients are refused, 30 accepted; an ingredient of 0 or 3,001 g is refused; oil reduced to a fifth is refused.

---

## 6. External integrations (Plate.Web/Integrations)

### 6.1 Food parsing via OpenRouter

Interface in Core:

```csharp
public interface IFoodParser
{
    Task<FoodParseResult> ParseAsync(string text, CancellationToken ct);
}
```

LLM nutrition estimates are unreliable on their own (published studies show energy errors around 35% and larger protein errors), while the per-100 g values in a nutrition database are not. So the model reads the text and the database supplies the numbers.

`FoodParseResult` contains a list of `ParsedFoodItem` plus an overall error message if parsing failed. `ParsedFoodItem`:
- `NameEn` (canonical English name, for matching), `NameOriginal` (as the user wrote it)
- `Grams`, `State` (Raw, Cooked, NotApplicable), `Preparation` (nullable, e.g. "grilled")
- `AmountGiven`: the text gives this food's amount as a weight, volume, count or fraction ("200 g", "2 eggs", "half a pack"); false when it gives none ("извара", "some rice", "a banana"; an article alone is not an amount)
- `GramsStated`: `Grams` is a weight or volume the text gives ("200 g", "165 g total"), which wins over any count
- `Units` and `CountOf` (Piece, Pack, Serving): a count instead of a weight ("две закуски" 2 pieces, "a banana" 1 piece, "половин кофичка" 0.5 pack, "2 servings"); null when the text gives a weight or no count. A stored `Units` without `CountOf` (runs from before it) counts pieces. The parser still estimates `Grams` for a count.
- `Assumption` (string such as "assumed one medium banana, 120 g")
- `EstimatedKcalPer100g`, `EstimatedProteinPer100g`, `EstimatedCarbsPer100g`, `EstimatedFatPer100g`, `EstimatedFiberPer100g`, `EstimatedSaturatedFatPer100g`: the model's own values, used only as a fallback

Implementation `OpenRouterFoodParser`:
- Typed `HttpClient` calling OpenRouter's `POST /api/v1/chat/completions`.
- Model: each AI task has its own model setting, shared by all accounts like the key (the parser, the matcher and the estimator below, the setup chat in 6.4, the weekly summary in 6.5 and the label reader in 6.6). The parser's is picked from OpenRouter's catalog (`GET /api/v1/models`, cached for an hour, limited to text models that support `structured_outputs` and `response_format`, `:batch` variants excluded), otherwise configuration `OpenRouter:Model`, otherwise `openai/gpt-6-luna`; a saved id must be in the catalog. The chat-model pickers (parser, estimator, setup chat, weekly summary, label reader) show only the 40 models of that list used most on OpenRouter last week (`sort=top-weekly`), none older than a year, no `~` aliases and none with an announced retirement date; the model already picked always stays. Any model in the catalog can still be set with `ai set-model`.
- API key from Settings (encrypted with Data Protection) or configuration `OpenRouter:ApiKey` (environment variable `OPENROUTER_API_KEY` in Docker). Never commit it.
- Request structured output: `response_format: {type: "json_schema", json_schema: {name, strict: true, schema}}` with a strict-compatible schema (`additionalProperties: false`, every property required, nullable ones typed `["string","null"]`) matching `ParsedFoodItem[]`, and `provider: {require_parameters: true}`. Because that excludes providers lacking any sent parameter, `temperature` and `reasoning.effort` (the effort picked for the task in the admin area when the model takes it, else the model's lowest supported effort; section 12) are sent only when the catalog lists them for the model.
- System prompt: the user writes foods in English or Bulgarian; return one item per food, where a baked good or ready-made product named by its filling ("закуска с лютеница и сирене", "кроасан с шоколад") is one food; estimate grams for vague quantities ("a banana", "a plate of rice") and state the assumption; assume raw weight for meat and cooked weight for rice and pasta unless stated; set `AmountGiven`, `gramsStated`, `count` and `countUnit` (piece, pack or serving; both null with a stated weight) as defined above, where a food named once without an amount that is sold or eaten as single pieces ("закуска с лютеница и сирене от лидл", "кроасан") is one piece with `AmountGiven` false, so the piece weight applies unless the account has a last amount, and food served in portions ("ориз", "супа") has no count; always fill `NameEn`. The reader keeps a count only with a known unit, above 0 and at most 100, and never with a stated weight.
- Catalog-backed model attempts start their deadlines before preparing the request body. The deadline covers catalog lookup, sending the request and reading the response. A failed catalog refresh uses the last successful list for three minutes.
- The parser allows 15 seconds per attempt and one retry on 5xx or timeout. On failure, the UI falls back to manual entry with the text preserved.

Resolving nutrition values (a Web feature service, not the parser):
1. **Usual food:** when the account has a `UsualFood` for the item's `NameOriginal` key, or else its `NameEn` key (5.10), and the account can still see that food (its own or a shared one, through the query filters), that food is the match with High confidence, marked "Your usual". A usual food whose raw or cooked state contradicts the item's (the state step 2 ranks by) is skipped, so "пилешко филе на скара" does not become the raw fillet confirmed for "пилешко филе". The ranking still runs so the run keeps its top 5 (section 11), but neither it nor the decision model chooses for that item.
2. Otherwise match `NameEn`/`NameOriginal` and `State` against the user's own `FoodItem`s, then shared items including the USDA subset (PostgreSQL `pg_trgm` similarity on names). A ranking adds bonuses for the user's own foods, the food's main word, the right raw or cooked state, and fewer niche variants, and compares the top two distinct grouped candidates. A ranking pick requires identifying food words in the English or original name, compared after a light ending stem for Bulgarian (one plural, article or final vowel ending, so "ябълки" meets "Ябълка" while "суши" and "круши" stay apart; a food's word in "-е" also meets its plural in "-ета", so "кюфтета" meets "Кюфте"). The ranking compares Bulgarian names the same way, and lowers a food whose main words (before the first comma) are none the user wrote although another of its words is: "кюфтета" in "Еленско месо, кайма, кюфтета, печени" is a detail of elk patties, not кюфте. A partial match needs its main phrase, at least two matched words and three quarters of its identifying words, and a lead of 0.1 over a runner-up. Bulgarian typed in Latin letters ("boza", "kebap4eta", "mlqko") meets no Cyrillic-only product name, so when an item's original name has only Latin letters and nothing above holds up, its Cyrillic reading (`BulgarianLatin` in Core: the official transliteration plus the informal q, w, 4 and 6; a best guess, as "a" may be "ъ") is searched and ranked as Bulgarian too, counts as identity evidence against the Bulgarian names and joins the decider's shortlist. Only then: an English name read in Cyrillic is noise, and a supported match needs no help. The row keeps what the user typed. Otherwise step 6 supplies the estimate.
3. When a matcher model is chosen in Settings and a key exists, a decision model chooses instead (below). Otherwise, and whenever it gives no usable answer, apply step 2's identity check to the ranking.
4. **Amount** (`Portions.For` in Core): a stated weight is the grams. A count uses the matched food's weight for what it counts: `PieceGrams` for pieces, `PackGrams` for packs, `DefaultServingGrams` for servings, noted as "2 × a piece of 90 g" (and the parser's assumption, which was about its own guess, is dropped). Without that weight the parser's grams stand, noted "No weight per piece for this food, so check the grams." (or the pack or serving version) unless the parser's assumption already says how it guessed; a serving is never used as a piece, and the usual serving never as a pack. For a weighed staple (rice, pasta, oats, bulgur, couscous, buckwheat, lentils, beans, chickpeas and the like, `FoodStates.Staple`; not foods made from them such as rice cakes, flour or soup) whose state the typed words don't give ("сварен", "суров", "dry"), when the matched food is that staple and its state is known from its name or else its energy (250 kcal per 100 g or more is dry, 200 or less cooked), the row asks "When did you weigh it?" with "Before cooking" and "After cooking", the other one being the plain shared food of that staple in the other state. The grams stay; the food and values change, with the note "Counted as weighed after cooking." (or before). When the matched food is the other way from the parser's reading (cooked for rice and pasta) and it isn't the account's own or usual food, the row starts on the food that fits the reading, with that note ("около 200г басмати ориз" matched a 357 kcal dry product and made 714 kcal). The question is skippable and stored with the review; picking another food with Change food removes it. The eval counts the items it would be asked for. Then, when `AmountGiven` is false and the account has a last amount for the matched food (5.10), the grams are that amount and the amount notes give way to "Your usual amount, {0} g". An amount the user gave is never replaced, and an estimate (step 6) keeps the parser's grams. Test: "Две закуски с лютеница и сирене от Лидл" (Lidl's 90 g piece) gives 180 g and about 419 kcal with the estimated-values note; "two of those pastries, 165 g total" gives 165 g; two biscuits from a "2 biscuits (30 g)" serving give 30 g; half a 400 g tub gives 200 g, and half a tub of a yogurt with only a serving keeps the parser's grams with the pack note; "a biscuit" with a last amount takes the last amount.
5. If a match is found, compute totals from its per-100 g values. The row is named after the matched food (its name in the account's language), shortened by the namer below, with the full database name under it and a control to pick a different match. A food whose `ValuesEstimated` is set adds "These values are estimated from the recipe, not read from a label." Picking another food renames the row after that food. A row whose amount was a count keeps the count, and picking another food recomputes its grams from that food's piece, pack or serving weight (with the matching note), unless the user has edited the grams since.
6. If not, the item is an estimate, marked Low confidence. Its values come from the **food estimator**, an AI task of its own (`estimator`, setting `OpenRouter:Model:Estimator`, default the parser's model, Off keeps the parser's guesses). After matching, one call sends what the user wrote and, for each unmatched item, the typed name, English name, grams, state, preparation and the five closest database foods the ranking found with their values, which were judged not to be this food and serve only as a reference for a close relative. It returns kcal, protein, carbs, fat, fiber and saturated fat per 100 g. The parser still guesses values for every item, and they stay when the estimator is off, fails, takes over 12 s, or gives values `FoodMath.Validate` refuses or no energy. When it was asked and gave nothing usable for a row (any of those but off), the row also says "The detailed estimate didn't come back, so these are rough numbers. Check them.", kept with the run's notes; with the estimator off nothing was asked and the row says nothing more. The grams are always the parser's. Rows matched to a database food are never sent. If `|Kcal - (4*Protein + 4*Carbs + 9*Fat)|` exceeds 15% of `Kcal` for the values the row ends with, also say so. The estimator model is kept on the parse run's row (`estimateModel` in `Resolved`). Recipe ingredients typed as text (5.12) get the same.
7. Reject negative values or grams above 3,000.

Matcher (`IFoodMatchDecider`, interface in Core; `JevFoodMatchDecider` in Integrations):
- Settings offers Off (the ranking alone, the default) and every decision model OpenRouter lists (`GET /api/v1/models?output_modalities=decisions`, cached for an hour), except aliases such as `~typesafe/jev-latest` (they can move to a new version silently) and models with an announced retirement date. The thresholds below were measured on `typesafe/jev-1.13`; another model is evaluated in the admin area (Eval) before it is picked.
- Deterministic retrieval also searches the structured brand with each name. A brand bonus requires agreement with the product words. A brand counts as written with or without its spaces ("кроасан 7days" names "7 Days"; QA 2026-11-24), here and where a written brand keeps its group apart (below). Preparation, flavour, fat level and a stated percentage distinguish variants. An unspecified variant favours a plain food. A brand written as the name of a product type (Coca-Cola, Coke, Pepsi) is looked up as that type, and a USDA food the words match only in its manufacturer field (a company, or the first of two all-capital fields: "The COCA-COLA company, Minute Maid, Lemonade", "COCA-COLA, POWERADE") is not that food.
- Per item, a shortlist of the plain trigram top 50 (similarity of at least 0.3 on `NameEn`, on `NameOriginal` when it differs, and on the Cyrillic reading when step 2 searched one, merged by the better similarity, best first; the ranking is not used). Jev leans toward the first option, so the order is fixed.
- Same-named products are one option, in the shortlist and in the ranking: products without an English name of their own that share a Bulgarian name (six makers' "Лютеница") go in as the generic food of that name when there is exactly one, otherwise as the product with the middle energy, in the place of the group's best member. A brand the user wrote keeps its group apart. Foods with their own English names, the account's own foods and shared foods are never merged, and nothing is deleted: a barcode still finds its product. (Parse run 22: offered side by side, the six "Лютеница" rows drew a pick at 0.14.)
- One `POST https://openrouter.ai/api/alpha/decisions` per parse with the same key. State: the typed text and every item (as written, English name, state, preparation, grams). One Choice question per item that has candidates; each option names the food in English and Bulgarian, its brand, and kcal and protein per 100 g, plus a `none` option.
- When the ranking supports the identity of its top candidate and that food is the user's own, or the item has a usual food (step 1), it stands: that item is sent as context only, not as a question.
- A picked food is the match: High confidence when Jev's confidence is 0.95 or more, otherwise Medium. A pick below 0.3 leaves the item to the ranking when the ranking's top score is 1.2 or more, and is no match (step 6) otherwise: the decider spreads its confidence over identical rows, such as five "Лютеница" rows ranked 1.70 that drew a pick at 0.14. `none` at confidence 0.9 or more is no match (step 6). A `none` below 0.9 leaves the item to the ranking only when the ranking's top score is 0.8 or more, and is no match otherwise. An option that was not offered, or a missing answer, leaves that item to the ranking. (The 0.3, 0.8 and 1.2 floors come from feedback #7–#10, where Lidl bakery items matched a SUBWAY sandwich, a breakfast pizza and tea biscuits; no pick or fallback users kept was below them.)
- 3-second timeout, no retry; any error, timeout or unreadable response falls back to the ranking silently. One log line per call with the version, latency, input tokens and cost (input tokens only, about $0.0002 per parse).
- Jev's main training language is English. It was evaluated on Bulgarian and English entries before adoption (gated rule: 29 of 32 held-out items against the ranking's 24), but its Bulgarian accuracy is the thing to watch.

Naming (`IFoodNamer` in Core, `OpenRouterFoodNamer` in Integrations):
- After matching, one call per parse with the parser's model and request shape, at the same time as the estimator's call (6.1 step 6): the namer names only matched rows and the estimator fills only unmatched ones, so the user waits for the slower of the two, not both (strict structured output, `provider: {require_parameters: true}`, temperature and lowest effort only when listed). For each matched row it sends the database name (and the English name for a Bulgarian account) and what the user typed, as context only. Schema: per row `index`, `name` (2 to 6 words, at most 60 characters, without how the food was prepared) and `status` (one or two lowercase words the database name says: raw, cooked, boiled, grilled, fried, baked, roasted, steamed, dried, canned, frozen, smoked, pickled, processed; null when it says none), in the account's language. In Bulgarian the status agrees with the name in gender and number ("Ориз" варен, "Паста" варена, "Пилешки гърди" сурови).
- The server keeps a name or status only when it can't mislead (`FoodNaming` in Core): not empty, within its length, no leading amount, and no raw or cooked state the database name contradicts or doesn't say. Otherwise the database name stands, and the status falls back to raw or cooked as the database name says. An estimate keeps what the user typed, its first letter upper case like the database names ("голям дюнер" shows as "Голям дюнер"), with the parser's preparation as its status.
- 5-second timeout, no retry; any failure leaves the database names. Without a key, no call. The answer is kept in the run's `Resolved` even when rejected. One log line per call with the model, latency, tokens and cost.

Display: a food entry reads "Chicken breast · 200 g" with its status as a chip beside it, on the confirmation list (where the grams are their own field), the food log and the entry page. A Bulgarian status is made to agree with the name it sits beside when shown (`BulgarianAgreement` in Core): a known state word (суров, сготвен, варен, печен, пържен and the like) takes the ending of the name's gender and number, read from its head noun, so the neuter fallback "сготвено" reads "сготвен" beside "Ориз", and rows saved before this read right too. The amount is drawn from `Grams`, never stored in the name.

Usual foods: confirming a parse writes, for each saved item that has a food, its `NameOriginal` key (5.10) and that food as a `UsualFood`: a new row, or the existing row moved to this food, with `LastUsedAt` set. The latest save wins, so picking another food once moves the words to it. Unticked items, estimates and names without a key write nothing, and neither does an item saved on the food the row suggested while marking it "Check this": the app was unsure and the user didn't choose, so remembering it would bring the same food back as "Your usual" with no warning (QA 2026-11-24, "кафе с мляко" as a sweetened bottled coffee). Picking another food on such a row is a choice and is remembered. The feedback eval's replay of usual foods (section 11) follows the same rule. No model is involved, so this works the same without a key, and an item with a usual food is not a matcher question, so it costs nothing.

Results are never saved directly. The user always sees an editable confirmation list first. Each successful parse has its own account-scoped review address. Reload or Back restores the review without a second model call. The browser keeps edits to recent unsaved reviews in the same tab and clears the draft when the save is confirmed. Opening or submitting a saved review returns to the original food log with an already-saved notice. Saved entries record `AiModel`.

Also provide `FakeFoodParser` for tests and local development without a key (selected when no OpenRouter key is set). It sets `AmountGiven` when it read a weight or a count other than "a", "an", "един", "една" or "едно", `GramsStated` for a weight, and a count as pieces.

### 6.2 Open Food Facts

`IFoodDatabase` with `GetByBarcodeAsync(string barcode)` and `SearchAsync(string query)`.
- Use API v3 (v2 is deprecated): `GET https://world.openfoodfacts.org/api/v3/product/{barcode}` with a `fields` parameter limited to name, brands, nutriments and serving quantity. Confirm the v3 field names against the current OFF docs before coding.
- Read `energy-kcal_100g`, `proteins_100g`, `carbohydrates_100g`, `fat_100g`. If kcal is missing but kJ is present, convert by dividing by 4.184.
- Also read `fiber_100g` and `saturated-fat_100g` when present. They are optional: a product without them still counts as having nutrition data.
- `PieceGrams` comes from `serving_size` only when it counts pieces of the `serving_quantity` ("2 biscuits (30 g)" gives 15 g; "30 g", "1 portion (30 g)" and "2 tbsp" give none), and `PackGrams` from `product_quantity` in g or kg, else a `quantity` such as "400 g" (`UnitWeightText` in Core). A volume ("500 ml") is not a weight and gives no pack weight. Products cached before these fields keep them empty.
- Set the `User-Agent` OFF requires: `Plate/<version> (<contact email from config>)`.
- Rate limits are 15 product reads and 10 searches per minute per IP, shared by all users because all calls leave from wizard. Call OFF search only on explicit submit (never as-you-type), and treat HTTP 429 as "try again shortly" without breaking the page.
- Cache every successful lookup as a shared `FoodItem` so repeat scans are instant and work offline from the API.
- The name is `product_name`, else `product_name_en`, `generic_name` or `generic_name_en`. With none of them, the brand and `quantity` stand in ("Monster 500ml"; "Product <barcode>" without a brand) and the item is `Unnamed`. Its barcode result then has a name field above the grams, filled with the stand-in, with "Open Food Facts has no name for this product. The name you give it is saved for everyone." The first name saved replaces the stand-in on the shared item for everyone, along with that user's entries still carrying it; later names are ignored. A logged packaged food reads "name (brand)", without the brand when the name already starts with it.
- If the product is missing or has no nutrition data, offer to create a manual `FoodItem` with that barcode (shared, see section 4).

### 6.3 USDA FoodData Central subset

Generic foods (chicken breast, cooked rice, banana) come from USDA FoodData Central, which is public domain (CC0).
- A one-off script, committed to the repository, converts the Foundation and SR Legacy downloads into a trimmed CSV (name, kcal, protein, carbs, fat, fiber and saturated fat per 100 g, the last two blank where USDA has no value, raw/cooked where the name states it) under `src/Plate.Web/Data/Seed/`.
- On startup, if no `Usda` items exist, the app imports the CSV as shared `FoodItem`s with `Source = Usda`. No runtime calls to the USDA API.
- Databases seeded before the CSV had fiber and saturated fat get them once, matched by English name, the way Bulgarian names were added. Rows that already have a value are left alone.
- The Bulgarian foods CSV (`bg-foods.csv`) has the same two optional columns. Products get them from Open Food Facts by barcode (`tools/foods/add-fiber.mjs`), and existing databases get them on the next start where both are still unknown. Dishes stay blank, since their source pages don't give them. Two more columns, `piece_g` (one piece's weight) and `basis` (`label`, `estimate`, or empty for a composition table or Open Food Facts), fill `PieceGrams` and `ValuesEstimated`; so far only the Lidl bakery rows (`tools/foods/lidl-bakery.csv`) and "Кафе с мляко" (`tools/foods/mixed-drinks.csv`, computed from USDA brewed coffee and whole milk, 4 to 1, so "кафе с мляко" has a food of its own rather than a sweetened bottled coffee) have them. Generic foods seeded before the columns existed get them on the next start, by Bulgarian name, where the piece weight is still empty or the estimate flag unset. Open Food Facts products whose values are wrong enough to mislead (`retired-products.csv`, by barcode and the energy they had) are left out of the seed and removed from databases at start while still at that energy; logged entries keep their totals. The first is a 134 kcal "Мюсли" that, as one of two same-named products, stood for every typed muesli.

### 6.4 Setup chat (onboarding)

A new account starts with an empty profile, so on first sign-in the user gets a short setup chat that fills it in.

**When it shows.** A signed-in user whose `OnboardedAt` is null is redirected to `/welcome` on any full-page GET navigation (not htmx requests, POSTs, static files, or the account, admin, feedback, passkey, CSP-report and welcome paths), with the page they asked for as the return URL. Finishing or skipping sets `OnboardedAt`. Settings has "Run the setup chat again". Accounts that existed before this feature see it once too, with what is already known filled in.

**Flow.** A splash (greeting, "Let's set you up", "Skip for now", and the line "Your answers are sent to an AI model through OpenRouter. Prefer not to? Use the form.") opens the chat. Each user message (up to 500 characters) goes to the server, which calls the model and shows its reply with up to four quick-reply chips. The server, not the model, decides when everything required is known: sex, birth year, height, current weight, activity level, goal, and the weekly rate unless the goal is Maintain. Starting maintenance is optional; protein defaults to Auto (5.6). Then a summary card appears under the chat: every field as an input within the 5.7 limits, the starting maintenance and today's target computed from them, and Save. The user can keep chatting and the card is redrawn. Nothing is saved until Save, which goes through the same profile validation as Settings, writes today's weigh-in if the weight differs from the latest one, saves the browser's time zone, and sets `OnboardedAt`. After 30 turns the chat stops and only the form remains. Without an OpenRouter key, or with the onboarding model set to off, and whenever a call fails, the same form is offered without the chat.

**State lives on the server.** `OnboardingChat` (one row per user, owner query filter): the draft (the fields gathered so far), the messages, the turn count and the update time. Each turn sends the model the current draft as JSON, the fields still missing, the reasons any value from the last turn was refused, the 5.7 limits, and the last 20 messages. The model's answer only proposes values; each is checked against 5.7 and an out-of-range one is dropped and named in the next turn. The row is deleted when the user finishes or skips.

**Model call.** `IOnboardingAssistant` in Core, `OpenRouterOnboardingAssistant` in Integrations, with the same request shape, structured output, timeout, retry and error messages as the food parser (6.1). Schema: `reply` (string), `quick_replies` (up to four short strings), and `fields` with every profile field from the list above, each nullable. A third AI task, `onboarding`, has its own model setting; unset, it uses the parser's model.

**Prompt rules.** Warm and brief; one or two questions per turn; say in a few words why each item is needed; for values already known, confirm instead of asking again; never give medical advice or comment on the user's weight; reply in the account's language. Activity level is classified from the user's job, daily steps and training, using the Settings descriptions (section 7); activity is everything on top of daily living, and three or four one-hour lifting sessions a week count for about half a level, added to walking rather than a level by themselves; when unsure between two levels, ask one follow-up, and if still unsure pick the lower one.

**Limits.** Rate limit of 40 chat messages per 10 minutes per user (section 9).

### 6.5 Weekly summary

A short note on Today about the previous Monday to Sunday: what went well, up to three concrete tips, and one line of encouragement. It is written by a model from the week's numbers, so it can be specific ("protein reached the minimum on 2 of 6 days") instead of generic.

**When.** On the first Today visit from Monday, if the account has it on (`WeeklySummaryEnabled`, a Settings switch, on by default), a key is set and the weekly model isn't off. One summary per user and week is stored and shown all week; it is written again only if the account's language changed since. Today renders a stored summary directly. When none is stored yet, Today shows a placeholder that loads it from `/today/weekly-summary`, so the page never waits for the model. That endpoint is rate-limited to 6 calls per hour per user (section 9); a failed or refused call shows nothing and the next visit tries again.

**Enough to say.** Only with at least 3 days of food logged (200 kcal or more), or a finished workout, or 3 weigh-ins in the week. Otherwise nothing is written for that week.

**The facts sent.** No name, email or notes (exercise names are sent, including custom ones): the goal and weekly rate; food (logged days, averages of kcal, protein, fat, carbs, fiber and saturated fat against the targets in force, the days protein reached its minimum, using 5.8 with the week as its range); body (trend weight at the start and end of the week, weigh-in count); training (finished sessions, working sets, new e1RM records by exercise name, the most trained muscles); and whether a diet break ran. Today's targets stand in for the week's.

**Model call.** `IWeeklySummaryWriter` in Core, `OpenRouterWeeklySummaryWriter` in Integrations, with the same request shape, structured output, timeout, retry and error handling as the setup chat (6.4). Schema: `summary` (string), `tips` (array of strings), `motivation` (string). The answer is trimmed to the lengths above, empty tips are dropped, and an empty summary counts as a failure. A fourth AI task, `weekly`, has its own model setting; unset, it uses the parser's model, and off turns the summary off for every account.

**Prompt rules.** Plain and warm, in the account's language, using the informal "ти" in Bulgarian. Mention real numbers from the facts and never invent one. Tips are specific actions for next week drawn from the weakest numbers (for example protein at breakfast when the minimum was missed); when a number has no target or too little data, leave it out. No medical advice, no comments on the user's body or appearance, no praise or blame for weight changes; describe the trend against the goal neutrally.

### 6.6 Label reading

Packaged products missing from Open Food Facts are entered from their nutrition table. Typing six numbers is slow, so a photo of the table fills the manual form instead, and the user checks it and saves. The values are printed on the pack, so the model transcribes rather than estimates; this is the one place a model's numbers go into a food directly, and only after the user has seen them.

**Where.** "Read the label from a photo" at the top of the manual form, both on the Manual tab and after a barcode lookup finds nothing or no nutrition data; and "Photograph a label" on the Scan tab, under "Scan a barcode", for products without one, which opens the manual form filled in. Hidden when no key is set or the label model is off; disabled offline ("Needs a connection"). Under the button: "The photo is sent to an AI model through OpenRouter and kept for 30 days so misreads can be checked."

**Photo.** `<input type="file" accept="image/*">` without `capture`, so iOS offers both the camera and the photo library. The phone draws it at most 2,000 px on its long side as JPEG quality 0.85, stepping down like the feedback screenshot (section 11) until it is under 2 MB, with the same resize code. The barcode detector (section 7) also runs on the photo, and a barcode it finds fills the form's barcode field unless one is there. The server accepts one to three photos, each JPEG, PNG or WebP by its first bytes and up to 2 MB; `POST /food/label` is the one endpoint whose request body may be up to 7 MB (section 9).

**More photos.** A label often splits what's needed across sides of the pack: the table on the back, the name and serving weight on the front. One photo reads at once, and only then, in the form it filled, "Add more photos" appears under the photo button: nobody has to decide before seeing whether the first photo was enough. It swaps the button for three slots, "Nutrition table" and two "Optional", with the line "Add the front or another side of the pack if the table doesn't show the name or serving." The slots start with the photos the form was filled from, kept on the phone after the read. Picking into a slot (camera or library) shrinks the photo like a single one and shows its thumbnail with a remove button; nothing is sent. "Read the label" is enabled once a slot holds a photo that hasn't been read and sends every filled slot, in slot order, in one read that replaces the form; "Cancel" closes the slots and drops the new photos. Slots and Read are disabled while a read runs; a failed read leaves the slots filled so a photo can be changed and read again. A name or "You ate" the user typed or changed goes with the new read and is kept; one the earlier read filled in is left to the new read, so the front of the pack can supply a better name than the table did. The barcode detector runs on the photos in order while the barcode field is empty. A read is one LabelScan with all of its photos and counts once toward the limit, so a food read again with more photos counts twice.

**Model call.** `ILabelReader` in Core, `OpenRouterLabelReader` in Integrations, with the parser's request shape (6.1): strict structured output, `provider: {require_parameters: true}`, and temperature and the lowest reasoning effort only when the catalog lists them. Each photo is an `image_url` content part with a base64 `data:` URL, in the order taken. Timeout 20 seconds and one retry on 5xx only; a timeout is not retried, so nobody waits more than about 40 seconds. Schema: `is_label` (bool), `name` and `brand` (nullable, only when printed in any of the photos), `basis` (`per_100g`, `per_100ml` or `per_serving`), `serving_grams`, `energy_kcal`, `energy_kj`, `protein`, `carbs`, `fat`, `saturated_fat`, `fiber` (nullable numbers, as printed in the chosen column).

**Prompt rules.** Transcribe; never calculate or estimate. Prefer the per-100 g or per-100 ml column, and use a per-serving column only when there is none, giving the serving weight. A value that isn't printed or isn't legible is null. Read decimal commas. Labels are in Bulgarian or another EU language; name the Bulgarian rows (Енергийна стойност, Мазнини, от които наситени мастни киселини, Въглехидрати, от които захари, Фибри or Хранителни влакна, Белтъчини, Сол), and say sugars are not carbs. A photo that isn't a nutrition table gives `is_label` false. Up to three photos show the same package: take each value from whichever photo prints it clearly, and the earlier photo's when two print different numbers for the same row. `is_label` is false only when no photo shows a nutrition table. `name` and `brand` may come from any photo.

**Normalizing** (`LabelValues.Normalize` in Core, pure, unit-tested with each case below):
1. `is_label` false, or energy, protein, carbs and fat all null: "Couldn't find a nutrition table in that photo." The form is left as it was.
2. kcal is kJ ÷ 4.184 when kcal is null. When both are printed and differ by more than 5%, kcal is kept with a note.
3. Per serving becomes per 100 g through `serving_grams`. Without a serving weight: "The label only gives values per serving. Enter them by hand or photograph the per-100 g column." Converted fields are marked.
4. Per 100 ml keeps its values and makes the food's unit ml (section 4), with the note "The label gives values per 100 ml, so amounts of this food are in ml." The form's "Per 100 g / Per 100 ml" choice is set to ml, and the serving and "You ate" show ml. The user can switch it before saving; a switch counts as an edit of the scan.
5. Rounded to one decimal, kcal to a whole number. A value outside the `FoodMath.Validate` limits is left empty with a note. Protein, carbs and fat adding up to more than 100 g, saturated fat above fat, and the 6.1 energy check (`|kcal - (4P + 4C + 9F)|` over 15% of kcal) each add a warning and keep the values.
6. A printed serving weight fills "One serving" and an empty "You ate". Amounts typed before the photo are kept: "You ate" always, "One serving" when the label prints none. The share choice is kept too.

**Result.** The form is filled (the name only when the field is empty), with the banner "Read from the photo. Check each value against the label." and any notes and warnings; converted and missing fields are highlighted. Nothing is saved until Save and add. Saving records the saved values on the scan (`Saved`, `Edited`, `FoodItemId`); a missing or foreign scan id never blocks saving food. A timeout, a refusal or an unreadable answer shows "Couldn't read the label. Enter the values by hand." and leaves the form untouched. Every read stores a `LabelScan` and its photo (section 4), failures included, and so does a read whose request was aborted (a second photo, leaving the page), since the model may already have been paid for. A read that fails before a model is chosen records the configured model, or "unknown".

**Model setting.** A fifth AI task, `label`, with its own model setting (`OpenRouter:Model:Label`), default `google/gemini-3.5-flash-lite`. Its picker lists catalog models whose `architecture.input_modalities` include `image`, with the parser's structured-output filter; Off hides the feature. It never falls back to the parser's model. About $0.001 to $0.002 per scan. Tests use `FakeLabelReader`; without a key the feature is hidden, so production never uses the fake.

**Limits.** 20 label reads per user per 10 minutes (section 9).

---

## 7. Screens

Mobile-first, designed for one-handed use on an iPhone. Bottom tab bar: **Today, Train, Food, Progress, Settings.** Dark theme by default.

### Today
- Calories remaining and protein remaining as two large progress bars, with consumed and target numbers. The protein bar counts toward the aim and marks the minimum.
- Under them, three small meters: fat (floor to ceiling), carbs (the range) and fiber (the minimum).
- Cut check-ins (5.9) and a diet break in progress, under the meters.
- Last week's summary (6.5), with the line "Written by AI from your numbers."
- Weight entry field (pre-focused if no weigh-in today), with the current trend weight shown next to it.
- Next suggested workout: the template after the last one completed, in template sort order, with a Start button. If a session is active, a Resume button instead. Once a workout was finished today (the user's day), a "Done today" line with its name, duration and working sets comes first, and the next workout shows as a smaller row under it.
- Small text showing the maintenance estimate and whether it is Initial or Adaptive, with days of data.

### Train
- List of templates with Start buttons; option to start an empty freestyle session.
- **Active session view:**
  - One card per exercise in order. Each card shows the progression suggestion and its reason, a "Last time" line with the previous session's working sets for that exercise, then a row per working set with weight and reps inputs pre-filled from the suggestion, and a check button. Without a suggestion, rows after a logged set are pre-filled from the last set logged in that card.
  - A logged set's weight and reps can be changed in place; the change saves on its own (no second tap) and the row shows whether it is saving, saved, or not saved yet.
  - Set rows are numbered by the server, so the numbers stay right after a set is deleted or added.
  - Each exercise's menu has Notes & bar (a note shown under the exercise name, and an optional bar weight); the same item is on the template edit page.
  - Barbell exercises (or any with a bar weight set) have a Plates button: the plates per side for a weight, from the bar and plates in Settings. It finds the closest load at or below the weight with the fewest plates, starting from the last working set or the suggestion.
  - Swap an exercise for another one in this session only, while it has no sets logged (warmups included); same-muscle exercises are listed first, and the slot keeps its sets, rep range, increment and rest.
  - Tapping the check button saves the set immediately via htmx and starts the rest timer.
  - Add warmup set, add extra set, add exercise, reorder, and remove exercise. Added rows are stored on the server at once (the slot counts live on the session exercise), so they survive a reload; an empty added row can be removed by swiping it. Values typed into a row that is not logged yet are kept on the phone until it is logged.
  - The exercise menu shows Move up only when there is an exercise above, and Move down only when there is one below. A Reorder mode lists the exercises with drag handles and up and down buttons.
  - Rest timer pinned to the bottom of the screen: counts down from the exercise's rest time and plays a short sound at zero, with +30 s and skip buttons. "Next" names the next unlogged set of the exercise just logged, else the first unlogged set after it. An Undo toast shown during a rest sits above the timer, never on it. iOS has no `navigator.vibrate`, so there is no vibration. To make the sound reliable on iPhone:
    - Hold a Screen Wake Lock while a session is active (works in installed web apps since iOS 18.4), so the phone does not lock between sets. Re-acquire it when the page becomes visible again.
    - Create and resume one `AudioContext` on the check-button tap, so it is unlocked by a user gesture before the timer ends.
    - Always compute the remaining time from the stored end timestamp, never by counting ticks, because iOS suspends timers in the background.
  - Finish button ends the session and shows a summary: duration, total working sets, new e1RM records, and a "First time" list for exercises done for the first time (a baseline, not a record). The confirmation says how many planned sets are not logged. The summary's back link goes to Train.
- **History:** sessions list, newest first; tap to view or edit a past session.
- **Templates:** create, edit, duplicate (the copy goes to the end of the list as "Name (copy)"), reorder exercises, set rep ranges, sets, increment and rest per exercise. Every field saves on its own when it changes, with an inline error that keeps what was typed. Duplicate and Archive are in the page's menu.
- **Exercise picker** (adding to a template or a session): pick several at once, added in the order tapped; exercises already there are marked. When nothing matches the search, it offers to create a custom exercise with that name.

### Food
- Date selector (defaults to today), entries grouped by meal, daily totals at the top. Future days, and days before 2000 (which only come from hand-typed or broken links), can't be opened or logged; opening one goes to today. A past day shows the captured targets in force then (`NutritionTargetDay`, section 4), without applying today's profile. Days from before snapshots existed are captured on first use (section 4); days before the first estimate say when targets start.
- The totals card shows each macro against its target (5.6): protein against the aim with the minimum, fat and carbs against their ranges, fiber against its minimum and saturated fat against its maximum, with the coverage note when a day is under 80% (5.8). Each of Breakfast, Lunch and Dinner shows its protein, with a check once it reaches the per-meal aim.
- A This week card with the 5.8 averages.
- Add entry with four methods:
  1. **Text:** a textarea (one line to start, its text level with the send button, growing as it fills); submit sends to the parser and shows the editable confirmation list; confirm saves all items.
  2. **Barcode:** opens the camera scanner (EAN-13, EAN-8, UPC-A, UPC-E only); on detection, looks up the product and asks for grams, pre-filled with the account's last amount for that food (5.10) with "Last time" under the field, else the serving size, else 100 g. Under "Add to {meal}" is "Add and scan another": it logs the entry the same way and opens the Scan tab for the same day and meal with "Added: {name}" above "Scan a barcode", instead of the Food day, so the next product is one tap away. The camera starts on that tap, as it does now. Installed iOS web apps do not keep camera permission, so expect a prompt on each launch, and open the scanner without changing the URL (no `hx-push-url`), since route changes can re-prompt or stop the stream. Fallbacks on the same screen: a field to type the digits, and a "take photo" button (`<input type="file" accept="image/*" capture="environment">`) decoded with the same detector. Under the scan button, "Photograph a label" (6.6) for products without a barcode.
  3. **Search:** searches as you type: the user's own FoodItems and recent entries first, then shared items (including the USDA subset), then Open Food Facts on explicit submit. Names that start with the search (singular or plural) rank above names that merely contain it, and identical foods are listed once. A search in Latin letters where no result has every word typed is also searched in its Cyrillic reading (`BulgarianLatin` in Core, as in 6.1 step 2), whose results go first when one of their names starts with it: "boza" finds the "Боза" products, not "Borage, raw". Each result's grams are the last amount for that food (5.10), else its serving, else 100 g.
  4. **Recent / favourites:** one-tap re-log of 20 foods from the last 30 days, in the 5.10 order for the chosen meal (what this account eats at that meal comes first), each with the amount used last time, editable.
- The methods are tabs at the top of the add page (Type, Scan, Search, Recent, Manual, each its own URL): plain words with an accent bar under the selected one, sized to the screen so all five fit on one line at 320 px in both languages. Choosing one changes the contents in place: the title and the tabs hold still, the bar glides to the new word and the contents crossfade. A choice isn't a step to go back through: Back leaves the add page. A read or a barcode lookup that fails shows the same page again in place, with the error and what was typed; a failed read keeps the page's address.
- Edit or delete any entry (open it, or swipe the row). An entry's page changes its name, amount and meal (moving it to another meal of the day), and, for an entry with a food or an estimate, "Change food": the review's search sheet, where a pick at once puts that food on the entry at the same amount, with its name, unit and values, and clears the status chip so the new food's own raw or cooked shows ("Food changed."). For a parsed entry, the words typed for its row then mean the new food (6.1, usual foods), as picking it on the review would have done. A quick entry changes its numbers instead. Copy yesterday's meal into today, offered only when yesterday has entries for that meal. An entry's name never carries its amount; the grams are shown beside it.
- On the confirmation list, each item can be switched to another food from a search, and a weak database match is marked "Check this" rather than "Database". A food found through a usual food (6.1) is marked "Your usual", and an amount taken from the last one reads "Your usual amount, {0} g" under it.
- Quick add per meal: a name (optional), calories, and optional protein, carbs, fat, fiber and saturated fat, for restaurant meals and labels not worth saving. Quick entries stay out of Recent.
- Manual food (the Manual tab, and after a barcode lookup finds nothing): name, the values from the label under "From the label" with a "Per 100 g / Per 100 ml" choice (grams unless a label read says ml; the serving and "You ate" follow it), an optional serving and the amount eaten, with "Read the label from a photo" at the top (6.6). Except after a barcode miss, where the scanned code is already known, an optional barcode field comes after the name, with a small scan button beside it that opens the same scanner sheet and puts the code in the field instead of looking it up (the sheet's "Take a photo instead" too). When the field holds a code the app already has as a food (the user's own first, then a shared one; Open Food Facts is not asked), a card under it shows that food: "Already in the app", its name, kcal and protein per 100 g, "Added by another user" when that applies, and "Is this the same food?" with "Yes, use it" (the barcode result screen for that code) and "No, it's different". Rejecting keeps the form: for the user's own food the barcode is cleared ("You already have a food with this barcode, so this one is saved without it."); for a shared food the share choice is unticked and hidden ("Another food already shares this barcode, so this one is kept for you only."), and the form carries the declined code: a label read redraws the form with it (even one that started before the tap), an error keeps it, and the server saves the food privately for that code even if sharing arrives ticked, rather than refusing it as already shared. Changing the barcode drops the decline and checks the new code. The check runs when the field changes (a scan, or typing and leaving the field) and when a label photo found the code; offline it is skipped and the form works without it. Saving the food and logging the portion happen together: when the entry is refused (too many grams, a future day), no food is kept, so a retry isn't told the barcode is taken. When the form has a barcode, a "Share with other users" checkbox, ticked by default, sits above Save, with the hint "Others who scan this barcode get this food instead of entering it again. Your name isn't shown." (section 4).
- A shared manual food that another account added is marked "Added by another user" (never a name or anything else about the account): in the grams sheet after a scan, on its search result line, and in the sheet of an entry logged from it. The account that added it, a food whose creator is unknown, and every other kind of food show nothing.
- Recipes (5.12): the Recent tab has a Recipes group under Saved meals, each recipe with its kcal and protein per 100 g, its number of ingredients, an amount field (300 g to start) with Add, and Edit; under them, New recipe. The recipe page has the name, the ingredients as Check foods rows (Change food, the cooked-or-dry choice, per-100 g values, unticking leaves one out), "Add ingredients" with a text box whose words go through the parser and matcher onto new rows ("Add these", counted against the parse limit and not recorded as a parse run, since they aren't a meal; a weighed staple the parser read as cooked starts on the dry food, with the note "Counted as weighed before cooking."), a search whose results add a food at its serving or 100 g (recipes are left out), "Weight when cooked" (optional, "Weigh the finished dish without the pot. Left empty, the raw weights are used."), a line with the dish's kcal and protein per 100 g as the rows change, Save recipe and, for a saved one, Delete recipe after the app's confirmation sheet. A refused save comes back with the rows and what to fix. Without a key, typed ingredients use the fallback parser and search works as always. A recipe is then an ordinary food of the account: in search under Your foods, in Recent (with its current values, not those of the last entry), and in text parsing; an entry of one links to "Edit recipe" on its page. The cooked-or-dry question is never asked about a recipe.
- Saved meals: a meal's menu on the Food day saves its entries under a name. Saved meals are listed first on the Recent tab; tapping one opens a sheet with editable grams per item (quick items keep their numbers) and logs them all into the chosen meal. Rename or delete from the same sheet.
- Deleting a food entry or a set, and removing an exercise from a session or a template, happen at once with an Undo toast for 6 seconds (no confirm). Archiving a template, finishing or deleting a workout, removing a passkey, signing out everywhere and deleting a saved meal still ask first, in the app's own confirmation sheet (never the browser's `confirm`). The Undo toast belongs to the page it was shown on and goes away on another page; its timer pauses while it is pressed, and a failed undo says so.

### Progress
- Bodyweight chart: raw weigh-ins as dots, trend weight as a line, selectable range (4 weeks, 12 weeks, all).
- Per-exercise chart of best e1RM per session, with an exercise picker.
- Weekly volume per muscle group as a bar chart for the current and previous week.
- Calories: daily intake versus target for the last 4 weeks, with the maintenance estimate as a line.
- Protein: daily intake against the minimum and aim for the same 4 weeks, and the This week card (5.8).

### Settings
Settings is a short hub, not one long page (progressive disclosure: NN/g, and the iOS Settings pattern). The hub is grouped lists of rows; each row has an icon, a title, a grey line with what is set now (so most questions are answered without opening it) and a chevron, and opens one page with Back to Settings. No page goes deeper than that, except the custom exercise editor.
- Hub, in groups: Profile ("Name · 180 cm"), Goal & nutrition ("Cut · 0.5% a week · Low active"), Run the setup chat again; Training ("Bar 20 kg · Jump 2.5 kg"), Custom exercises; General ("Dark · English"), Sign-in & security ("2 passkeys" or "Password only"), Add to Home Screen (section 8); Send feedback, Your feedback; Export all data, and Admin for `Dev` accounts only; Log out, with the account's email under it.
- Every setting saves when it changes, with a "Saved" mark or an inline error under the card that changed; there is no Save button except for the password. Each page posts only its own fields (`/settings/profile`, `/settings/goal`, `/settings/training`, `/settings/time-zone`, `/settings/appearance`, `/settings/weekly-summary`), so saving one page never touches another's; the whole profile is still validated on every save.
- Profile page: email (read-only), name, sex, birth year, height, with the limits in 5.7. Inputs take their `min` and `max` from the same limits.
- Goal & nutrition page: goal, weekly rate, protein, starting maintenance, then activity level.
  - Protein: Auto (by goal) or a custom g/kg. Under it, the current aim in grams, and when the reference weight applies, "Based on {0} kg, a healthy weight for your height." A "How your targets are set" sheet gives each 5.6 rule in one line with its source.
  - Activity level, one line of explanation per choice. The levels follow NASEM 2023 table 7-1, which defines them as activity on top of daily living (about 30 minutes of walking): low active adds 60 to 80 minutes of brisk walking a day, active about two hours including some vigorous activity, very active more than two hours of vigorous activity. The step counts are a rough conversion.
    - Inactive: desk job and everyday errands, up to about 8,000 steps a day.
    - Low active: everyday life plus about an hour of brisk walking or similar every day, roughly 11,000 to 14,000 steps.
    - Active: everyday life plus about two hours of activity a day, some of it hard (cycling, a sport), or a job on your feet with lifting and carrying.
    - Very active: more than two hours of hard activity a day (running, cycling, sport), or heavy manual work plus training.
    Under it: "Lifting 3 to 4 times a week counts for about half a level; add it to your walking. Used only until about three weeks of logs give an adaptive estimate." (An hour of lifting is about 3.5 to 5 MET, so 200 to 320 kcal above rest for 80 kg; four a week is 115 to 185 kcal a day against about 220 kcal between inactive and low active.)
- Training page: weight jump; bar and plates (the profile bar weight and which plates the gym has).
- Custom exercises: create (name, primary muscle, equipment, optional secondary muscles), rename and archive the user's own exercises. They appear in the picker with the shared library and are never visible to other accounts.
- General page: theme and language; the time zone, picked from a list; the weekly AI summary on or off (6.5), with the line "Last week's numbers are sent to an AI model through OpenRouter."
- Sign-in & security page: passkeys (add, remove), change password, log out everywhere.
- Settings shows nothing about the AI models or the key. Only the developer manages them, in the admin area (section 12).
- Export all of the user's data as CSV files in a zip. Include private foods, saved meals and their ordered items, recipes and their ingredients, per-exercise notes and bar overrides, and profile preferences. Export their IDs so related rows can be joined. The food log, saved meal items and private foods have a `unit` column, `g` or `ml`, for their amounts and per-100 values.
- Feedback: Send feedback and Your feedback (section 11).

---

## 8. PWA and resilience

- Web app manifest per language (`/manifest.en.webmanifest` and `/manifest.bg.webmanifest`, both named "Plate": the name is an English word kept in Latin letters in both languages; the layout links the one for the account language, and the page title and iPhone home-screen label use the same name), icons (192 and 512, a weight plate with a bite out of it on orange, drawn in `tools/icons/icon.svg`; `icon-32.png` and `favicon.ico` use the simpler `tools/icons/favicon.svg`), `display: standalone`, dark theme colour, start URL `/today`.
- Add to Home Screen. Most people don't know a site can be installed, so the app says how. On Today, a phone browser (not the installed app, which `display-mode: standalone` or `navigator.standalone` identifies, and not a computer) shows a card, "Add Plate to your Home Screen", with "Show me how" and "Not now". "Not now" hides it for good on that device and account (`localStorage`, scoped to the account). Settings has an "Add to Home Screen" row in every case, opening `/settings/install`, which shows the steps for the browser in use: Safari on iOS (Share, under ⋯ on iOS 26, then Add to Home Screen, then Add), another iOS browser (the same through its own share menu, else open the page in Safari), an in-app browser such as Instagram's or Facebook's (open the page in Safari or Chrome first), Android (the browser menu ⋮, then Add to Home screen or Install app), a computer (open it on your phone), or the installed app (already done). Where the browser fires `beforeinstallprompt` (Chrome on Android and on computers), the card and the page offer Install, which opens the browser's own prompt. An iOS Home Screen app keeps its cookies and storage apart from Safari, so the iOS steps say a second sign-in may be needed. All text is rendered by the server and `install.js` only picks which part shows.
- Service worker that precaches static assets (CSS, JS including the vendored libraries and the scanner `.wasm`, icons) and one offline fallback page. Static files carry a content-hash query string, so the worker serves a cached copy of the exact URL without revalidating ("No connection. Sets you log are saved on the phone and will send when you're back online."). Pages and data are otherwise always fetched from the server. The files under `wwwroot/lib` are kept in a cache of their own (`LIB` in `sw.js`) that a new worker reuses, fetching only the library files it lacks, so a deploy that bumps the app cache doesn't download the 1.5 MB of libraries again; `LIB` is bumped whenever a file under `wwwroot/lib` changes, which `tests/js/sw-lib.test.cjs` enforces.
- Every rendered plain-POST form that adds food entries has a fresh request ID. A resend by the same account returns the first successful redirect and does not add entries again, even if its fields changed. Validation or a failed transaction leaves that ID available for correction and retry; a newly rendered form is a new intent. A form without a valid ID is refused with a request to check and reload. If the response or a page script fails after a POST, the warning says the change may have saved and asks the user to check before retrying. A failed page script can be loaded on the next attempt.
- Soft navigation follows the latest navigation intent, including Back and Forward. Only its response can load further page scripts, swap content, update history or trigger a fallback. Only the current navigation controls the loading indicator. Submitted POSTs run once even after a newer page is chosen, and the app discards obsolete page responses.
- **Set logging must survive bad gym signal.** iOS has no Background Sync, so the page drives all retries. The queue is write-ahead:
  - When the check button is tapped, generate the `ClientId` Guid, write the set (including `CompletedAt`) to the pending queue in `localStorage`, render it as "pending", and only then send it. A set is never only in flight; this way it survives the app being closed or suspended mid-request.
  - Sending uses plain `fetch` in a small module, not htmx. Read the anti-forgery token at send time rather than storing it with the set.
  - On success (including a duplicate `ClientId`), remove the set from the queue and replace the pending row with the server's rendering.
  - Each send attempt has a 15-second deadline covering the fetch and required response-body reading. A timeout keeps the set and its original `ClientId` pending for the existing retry triggers; the server may already have saved it. The deadline uses foreground timers, which iOS may pause while the app is suspended.
  - Retry while the queue is non-empty: on the `online` event, on `visibilitychange` to visible, on `pageshow`, at app start, after every successful request, and every 15 seconds. Only one send in flight at a time.
  - On a validation error (4xx other than 401), stop retrying that set and show the error on its row.
  - On 401, keep the queue and send the user to the login page; the queue resumes after login.
  - When the active session page loads, merge any pending sets for that session back into the view.
  - Call `navigator.storage.persist()` once in standalone mode to reduce the chance of eviction.
  - The server treats a repeated `ClientId` as success without creating a duplicate, after checking the existing set belongs to the current user, and returns the same response as for a new insert.
- The active session must be fully restorable after the app is closed or the phone locks: all state lives on the server, except the pending queue, values typed into rows not logged yet, and the rest timer end time (all in `localStorage`).
- Pending sets, feedback, drafts and rest timers are scoped by account. Queue requests carry the originating account id, checked against the signed-in user before antiforgery validation; a mismatch pauses replay without removing pending work. If local storage fails, queues keep an in-memory copy, continue sending, and warn the user to keep the page open; a message about a send that keeps failing the same way is shown once. Ownerless sets from before this change are adopted by the signed-in account; one the server does not find (another account's session) goes back to the ownerless queue for its owner and is not retried by that account, and ownerless sets older than 30 days are dropped. Ownerless reports and ratings are listed on Your feedback for the signed-in user to send from their account or discard; they are never sent automatically.
- Text never assumes there are only two users: the app is used by Antoan, his father and friends he invites. Shared things are saved "for everyone" or for "other users", never "for both of you" or "the other account"; the same goes for What's new entries.
- Numbers are written with a dot in both languages. The server also accepts a typed comma, since the Bulgarian iPhone keypad has one.

---

## 9. Security

- Public registration is disabled. Accounts are created with a CLI command, `dotnet Plate.Web.dll create-user --email ... --name ...`, which prompts for the password, or by redeeming a single-use invite from the developer (Invites, below).
- Every controller action requires authentication except login and the invite page.
- Global EF query filters plus explicit ownership checks on every write; integration tests must verify that user A cannot read or modify user B's sessions, sets, food entries or weigh-ins by ID.
- Anti-forgery tokens on all POSTs, including htmx requests (send the token via `hx-headers`) and the set queue's `fetch` calls.
- Secrets only from environment variables, except the OpenRouter key, which the developer may also save from the admin area (encrypted with Data Protection). Provide `.env.example`; `.env` is in `.gitignore`.
- Roles: one Identity role, `Dev`, granted and revoked only with the CLI over SSH (section 12). No web page grants a role, and the invite page is the only one that creates an account.

**Exposure.** The app is reachable at `https://plate.peychev.uk` through wizard's Cloudflare Tunnel, with no Cloudflare Access in front of it (decided 2026-10-02). Access would add a second login with an emailed code about monthly, and an expired Access session turns htmx and queue requests into failed cross-origin redirects. The app's own login is therefore the only gate and must be hardened:
- Lockout on: `PasswordSignInAsync(..., lockoutOnFailure: true)` (the template default is `false`), 5 failed attempts, 15-minute lockout.
- ASP.NET Core rate limiting middleware on the login POST, partitioned by client IP.
- Forwarded headers: behind cloudflared every request comes from 172.30.0.10, so configure `ForwardedHeadersOptions` with that address as the only known proxy, read the client IP from `CF-Connecting-IP` and the scheme from `X-Forwarded-Proto`. Without this, rate limiting puts every client in one bucket.
- Passwords: minimum 12 characters, no composition rules. An unknown email costs one password hash like a known one, so timing does not reveal which accounts exist.
- A second, global login limit across all addresses (30 attempts per 15 minutes). Per-user limits on password and API key changes (5 per 15 minutes), admin unlock requests (10 per 15 minutes, two per unlock), AI food parsing (30 per 10 minutes), setup chat messages (40 per 10 minutes), label reads (20 per 10 minutes), weekly summaries (6 per hour) and admin writes (60 per 10 minutes).
- Passkeys alongside passwords: registered in Settings, usernameless sign-in on the login page (button and autofill), relying party `plate.peychev.uk`.
- Response headers: Content-Security-Policy (`script-src 'self' 'wasm-unsafe-eval'`, no inline scripts or event handlers; sent report-only with a `/csp-report` log until `CSP_ENFORCE=true`), HSTS, `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: same-origin`, `Permissions-Policy` (camera only), COOP. `AllowedHosts` limited to the public host name. Request bodies over 1 MB are refused, except label photos (`POST /food/label`, 7 MB for up to three photos; section 6.6).
- Cookie: `Secure`, `HttpOnly`, `SameSite=Lax`, `__Host-` prefix, sliding 30-day expiration. "Log out everywhere" in Settings updates the security stamp.
- Data Protection keys persisted to a bind-mounted directory, so rebuilding the container does not log everyone out.
- The container publishes no host port. cloudflared reaches it by container name on the external `tunnel` Docker network. An optional LAN-only port may be added for testing (pick one that is free on wizard; 8080 is AdGuard's).

**Invites.** People Antoan knows join through a link he sends them himself (Viber, Messenger); the app never sends email, so there is no email verification, and the address is only the login name. The link is the proof that the developer meant to let this person in.
- Only accounts with the `Dev` role create invites, from the CLI (`invite create`) or the admin area (section 12). The token is 32 random bytes in base64url; the link `https://plate.peychev.uk/join/<token>` is shown once at creation, since only its hash is stored. Expiry 1 to 30 days, default 7. At most 5 invites are open (not redeemed, revoked or expired) at a time.
- `/join/{token}` is anonymous and in English and Bulgarian, defaulting from `Accept-Language`. It asks for name, email, password (the same 12-character minimum) and language. A token that is unknown, used, revoked or expired shows one message, "This invite is no longer valid. Ask Antoan for a new one.", and no form; the token's 256 bits make guessing pointless, so the page doesn't need to hide that the route exists. A visitor who is already signed in is told to log out first, and the token is not used.
- Redeeming runs in one transaction: a conditional update marks the invite redeemed (`RedeemedAt IS NULL AND RevokedAt IS NULL AND ExpiresAt > now`), and the account is created with the same `UserCreator` as the CLI, with the chosen language in its profile. If the update touches no row, or the account cannot be created (the email is taken, the password is too short), the transaction rolls back, the invite stays open and the form shows the error. Two submissions of one token create one account.
- After redeeming, the new account is signed in and lands on the onboarding splash (section 6.4). Passkeys are added in Settings as for any account.
- Join requests (GET and POST) are limited to 10 per IP per 15 minutes. The page sends `X-Robots-Tag: noindex`. Error events record the route template, never the token.
- A forgotten password is reset by the developer with `reset-password` (section 12); there is no emailed reset.

If Cloudflare Access is ever added in front of the whole app (it covers only `/admin*`, section 12), the spec must also require: a one-month Access session, `X-Requested-With: XMLHttpRequest` on every htmx and queue request (so Access returns 401 instead of redirecting), 401 handled as in section 8, and a Bypass policy for the manifest, icons and static assets.

---

## 10. Deployment

The stack lives at `~/docker/plate` on wizard, like the other stacks. wizard's boot SSD is failing and Docker named volumes live on it outside the nightly backup, so all state uses bind mounts under the stack folder.

**docker-compose.yml** with:
- `db`: `postgres:18`, container name `plate-db`, bind mount `./db:/var/lib/postgresql` (the 18 image moved its data directory to `/var/lib/postgresql/18/docker`; mounting the old `/var/lib/postgresql/data` path can silently put the data in an anonymous volume). Small-footprint settings: `shared_buffers=32MB`, `max_connections=15`, `jit=off`. `mem_limit` (start at 256m, adjust from Beszel readings). Healthcheck with `pg_isready`. Default network only.
- `app`: built from `docker/Dockerfile` (multi-stage build on the .NET 10 SDK image, runtime on `aspnet:10.0-noble-chiseled-extra`), container name `plate-app`, depends on a healthy `db`, no published ports, joined to the default network and the external `tunnel` network, bind mount `./keys` for Data Protection keys, environment from `.env`, `mem_limit: 256m` (adjust from Beszel readings), `cpus: "1.0"`, `restart: unless-stopped`. Workstation (non-concurrent) GC and a ReadyToRun publish for `linux-x64`; connection pool capped at 8. The container runs read-only (tmpfs `/tmp`), with all capabilities dropped, `no-new-privileges` and a PID limit. The .NET base images are pinned by digest and refreshed monthly (Postgres uses the floating `postgres:18` tag and is updated nightly on wizard); the build fails on high or critical NuGet advisories.
- No backup container (see below).

**docker-compose.dev.yml** for local development on a Mac: only `db` (`postgres:18`), published on `127.0.0.1:5432`, no `tunnel` network (it exists only on wizard).

The app applies EF migrations automatically on startup (including `CREATE EXTENSION pg_trgm`), adds any seed exercise the shared library lacks (matched by name) and imports the USDA subset if absent.

**Backups** go through wizard's existing nightly host script `/usr/local/bin/homeserver-backup` (03:30), which already writes dated snapshots to the HDD with 14-day retention, Telegram alerts on failure and the PC pull. Two changes to it, which need sudo and are run by Antoan:
- Add a dump step, e.g. `docker exec plate-db pg_dump -Fc -U gym gym > "$DEST/plate-db.dump"`.
- Exclude `plate/db` from the `~/docker` rsync, the same way `nextcloud/db_data` is excluded (a file copy of a running database is not a consistent backup).

Building the image on wizard pulls the .NET SDK image onto the failing SSD; prune the build cache after builds (`docker builder prune`), or build elsewhere for `linux/amd64`.

HTTPS (required for the camera and PWA install): add the public hostname `plate.peychev.uk` → `http://plate-app:8080` to the "wizard" tunnel in the Cloudflare Zero Trust dashboard. No Access application on it (section 9).

**README** must cover: prerequisites, `.env` setup, `docker compose up -d`, creating users and invites, resetting a password, deleting an account, the Cloudflare public hostname, the two backup-script changes, restoring from a dump (`pg_restore` into a fresh database), and installing the PWA on iPhone (Safari, Share, Add to Home Screen).

---

## 11. Feedback

Users report problems and ideas from their phones, food parsing collects ratings and the corrections people make, and errors are recorded on their own. the agent reads all of it over SSH with a CLI command and writes back a status and a short reply. Nothing is exposed on the public host beyond the app's own authenticated pages.

### Data

- **ParseRun** (user-owned): one row per text parse. `Text`, `AiModel`, `MatchModel`, `ParserMs`, `MatchMs`, `NamerMs`, `EstimateMs` (the estimator's call, which runs alongside the namer; null when no row needed an estimate) and `TotalMs` (the whole parse as the user waited for it, recorded whether it worked, timed out or failed; null for runs from before they were recorded, shown as "total unknown"), `ParserError`, `Parsed` (jsonb, the parser's items as returned), `Resolved` (jsonb, per item: the chosen food, confidence, notes, whether it came from a usual food, the ranking's top 5 candidates with scores, and Jev's raw answer when it ran), `Saved` (jsonb, what the user saved per item, null until confirmed), `Corrections` (flags), `ConfirmedAt`. `FoodLogEntry.ParseRunId` (nullable, set null on delete) links saved entries to their run.
- **Feedback** (user-owned): `ClientId` (Guid, unique, for the offline queue), `Source` (Settings, ParseRating, Barcode, Progression, Maintenance, FoodEntry, ErrorPage), `Kind` (Problem, Idea, Other), `Area`, `Rating` (Up, Down; parse ratings only), `Reasons` (flags: wrong food, wrong amount, wrong nutrition values, missed a food, extra item, should be split or merged, language not understood), `Message` (up to 2,000 characters), `Context` (jsonb: page, app version, user agent, viewport, standalone, language, theme, local time, recent client errors, and a snapshot of what the report is about), `ParseRunId` and `ParseItemIndex`, `ErrorEventId`, `Status` (New, Seen, Planned, Fixed, WontFix, NeedsInfo), `Reply` (up to 1,000), `StatusChangedAt`, `FixedInVersion`, `ReplySeenAt`.
- **FeedbackAttachment**: one image per report (`ContentType`, `Width`, `Height`, `Bytes`), in its own table so lists never load images. Stored in PostgreSQL so the nightly dump includes it.
- **ErrorEvent** (not user-facing, no user filter): unhandled server exceptions and browser errors, one row per fingerprint (type, message with numbers and ids removed, first in-app stack frame, path template) with `Count` (the lifetime total, incremented in the database so concurrent reports each count), first and last seen, last trace id, first and last app version.
- **ErrorEventDay**: `ErrorEventId`, `Day` (UTC), `Count`, incremented atomically with each occurrence, so a period's total adds up the days in it (whole UTC days; counted from when the table was added). Deleted with its group, and on its own after 90 days.
- **ChangelogEntry** (shared): `TextEn`, `TextBg`, `FeedbackId` (nullable), `Version`. `UserProfile.ChangelogSeenAt`.
- **AiCall** (user-owned): one row per OpenRouter call an account's request makes, for any AI task (parser, matcher, namer, onboarding, weekly, label): `At`, `Task`, `Model` (the one OpenRouter answered with, else the one asked for), `InputTokens`, `OutputTokens`, `Cost` (US dollars from the answer's `usage.cost`, which OpenRouter sends on every answer; null for a failed call), `DurationMs`, `Failed`. Never the text sent or received. A message handler in front of every AI client records it, so a new integration is covered by registering its client with it. Calls without a signed-in user (the CLI's `ai test`, `eval`, `pilot` and benchmarks) aren't recorded; an eval run from the admin area counts for the account that ran it. Calls from before the table existed are unknown, not free.
- Areas: Training (workout logging, progression suggestions, rest timer, templates, plate calculator), Food (text parsing, food matching, barcode scanning, food search, nutrition values, saved meals), Targets and progress (calories and maintenance, progress charts), App (offline and sync, sign-in and passkeys, Bulgarian wording, layout and design, speed, other).

### Food parsing

- Every parse stores a ParseRun. Confirming stores what was saved and compares it with what was suggested (`ParseCorrections` in Core): an item unticked, grams changed by more than 10% and more than 10 g, any per-100 g value edited, the description edited (on the same food; a new name that came with a different food is not an edit), a different food. This needs no tap. A missing or foreign run id never blocks saving food.
- The Check foods screen asks "Did this read your food right?" with thumbs up and down, and each item has its own thumbs down. A tap records at once without submitting the form; a thumbs down then opens a sheet with the reasons and an optional comment. An AI entry's page offers "Report a parsing problem" for the same run.

### Reporting

- Settings has Send feedback (kind, area, message, an optional screenshot) and Your feedback (own reports, newest first, with status and reply; a badge when a reply is unread).
- Contextual links open the same form with a snapshot of what was shown: a barcode result (the code and the food's per-100 g values), a progression suggestion (the inputs the Core service received and its output) and the maintenance estimate (the stored estimate and the window's daily intake and weigh-ins). A referenced row is loaded through the user's query filters, so a foreign id is not found.
- Screenshots are downscaled on the phone to at most 1,600 px and JPEG quality 0.8; the server accepts JPEG, PNG or WebP by their first bytes, up to 900 KB.
- Reports and ratings are written to a `localStorage` queue first and sent with the same retry rules as sets (section 8); a repeated `ClientId` is success. Screenshots need a connection.
- A report's inputs are locked while it is in flight. If storage fails, the queued report remains visible and locked in memory, and Send retries its original id and fields. A durably queued report leaves a fresh editable draft. Pre-upgrade clients that omit the queue owner are refused with 503 and `Retry-After`, so their existing retry logic retains the report instead of discarding it.
- Rate limits: 30 reports and ratings per user per 10 minutes; 20 browser error reports per user per 10 minutes, each at most 8 KB.

### Errors

- Unhandled server exceptions are recorded by an exception handler that never throws; validation and not-found results are not errors. Browser `error` and `unhandledrejection` events are sent once per page load to an authenticated endpoint, and the last five are kept in `sessionStorage` for the report form. The error page shows a short trace id and a Report this button.

### Notifications and what's new

- When `TELEGRAM_BOT_TOKEN` and `TELEGRAM_CHAT_ID` are set, new reports (not thumbs up) and new error fingerprints send a Telegram message from a background queue: the id, the reporter's name, kind, area and the first 80 characters. Never attachments, snapshots or food text.
- Today shows a dismissible What's new card with changelog entries newer than `ChangelogSeenAt`, in the user's language; an entry linked to the viewer's own report says so. The CLI writes the entries.

### Model accuracy

- The AI model pages in the admin area show, for each model used in the last 90 days, the number of parses, the thumbs-down rate and the correction rate, pooled across all accounts because the model setting is shared. Under 20 parses shows "not enough data".
- For the label model (6.6), the same page shows per model the number of label reads, the failure rate and the edited rate (saved scans with any value changed), with the same 20-read minimum.

### CLI

`dotnet Plate.Web.dll feedback <command>` (run on wizard as `docker exec plate-app dotnet Plate.Web.dll feedback ...`). Logs go to stderr so stdout can be piped. Text output, or `--json`.
- `list [--status new|open|all|<status>] [--since 7d] [--area <area>]`, `show <id>`, `attachment <id>` (raw bytes), `status <id> <status> [--reply ...] [--version <sha>]`.
- `parses [--since 30d] [--flagged] [--export-eval]` (an item matched through a usual food is marked "usual"), `eval [--parser <model>] [--matcher off|jev] [--limit N] [--no-history] [--stored-parse]` (re-runs the live parser and matcher on exported cases; never part of the tests).
- The matcher benchmark records the full deterministic pool size, first acceptable rank and ranking decision separately from the model shortlist, and for every model the path that chose each row (decider pick, ranking after an unsure answer, estimate and why), with how many rows each path got right. Its final score uses the catalogue label for a required match even when retrieval missed it. It reports selection precision, coverage, absent foods declined and required foods declined. Run the development and held-out family case files separately.
- An exported case keeps, per item, the suggested food, the user's verdict and reasons (a "wrong food" rating rejects the suggested food), and the saved row (ticked or not, name, grams, per-100 g values and food). `eval` scores *agreement with the saved log*, not accuracy, since a user can accept a bad suggestion: each saved item expects the food it was saved as, so after A was suggested and B saved, B agrees and A does not; a rejected food picked again is counted as a repeat; and each meal's kcal, protein, carbs and fat are compared with the saved grams and values (mean and median absolute kcal difference, signed bias, mean absolute macro differences). A kept food the user rated wrong, and cases exported before the saved row was kept (except accepted items, saved as suggested), count as "saved food not recorded" rather than as a miss. Another database row of the same food agrees (`FoodEquivalence`: the same main word and per-100 g values within 5% for kcal and 10% for macros, with small absolute floors), so a second "Bananas, raw" row is not a miss while lowfat and full-fat cottage cheese stay different. Each case also carries the entries the user logged within 30 minutes after confirming, outside the parse and not deleted (quick entries left out): a new item nothing saved accounts for that matches one of them is "logged after", not extra, and that entry counts in the saved meal's totals; these entries are never expected, since they may be other food. By default the replay uses the account's history as it was at the parse (`HistoryAsOf`): last amounts from entries made before it, and usual foods rebuilt from parses confirmed before it (latest save of the words wins, words unused for 180 days forgotten), so nothing learned after the meal helps; `--no-history` measures the parser and matcher alone. A run is skipped as superseded when the same account typed mostly the same words again (`ParseRepeats`: at least 60% of the shorter text's words of three or more letters) within 3 hours of confirming it and had not accepted it as parsed; the export keeps it, marked `supersededBy`, so its rejected foods still count in the regression tests. Each missed saved food says where it was lost: ranked among the matcher's top 5 for an item but not chosen (with the decider's pick and confidence when it was asked), or not ranked at all. `--stored-parse` matches the items the parser gave at the time instead of parsing again, so matchers can be compared on the same items. The account's own foods are visible, including any added after the meal, since foods have no creation time.
- `pilot [--file <csv>|-] [--account <email>] [--check] [--no-history] [--parser <model>] [--matcher ...]`: the weighed-meal pilot (`reports/weighed-meal-pilot.md`). Reads one row per weighed food (columns in `reports/weighed-meals-template.csv`; meals whose id starts with "example" skipped; every problem reported by line and nothing run until the file is clean), replays each meal's text as its account with the history as of `eaten_at`, and compares the totals with the weighed reference: per meal the kcal, protein, carbs and fat differences, then mean and median absolute kcal error, bias, macro errors, the same per kind of meal (counted, staple, homemade, loose), and the largest misses (`WeighedMeals` in Core). Meals with an item marked uncertain are shown but left out of the averages. One parse of credit per meal; `--check` calls nothing.
- `labels [--since 30d] [--edited]`, `label <id>` (the model's reading, the normalized values and what was saved), `label-photo <id> [n]` (the nth photo's raw bytes, 1 by default, while the photos are kept).
- `errors [--since 7d]` (each group with its lifetime count and how often it happened in the period), `digest [--since <date>]` (which also counts label reads, failures and edited scans; gives the median and 95th percentile of the whole parse time over the runs that recorded it; and per account the days with a food entry, set or weigh-in, food entries by source, sets, weigh-ins, and AI calls by task with failures and cost, busiest first, counts and money only, with a note when AI calls weren't recorded for the whole period), `changelog add --en ... --bg ... [--feedback <id>] [--version <sha>]`.
- The app version is the Git commit, passed at build time as `APP_VERSION` and stored as the assembly's informational version.
- Retention: ParseRuns older than 180 days with no feedback, LabelScans older than 180 days, label photos older than 30 days, UsualFoods not used for 180 days, AiCalls older than 180 days, and ErrorEvents last seen more than 90 days ago, are removed by the hourly cleanup job. The data export includes the user's own reports, label scans (without photos) and AI calls.

---

## 12. Admin

The developer's account (Antoan) has an admin area at `/admin` that does what the feedback CLI does, plus the AI settings and invites. Everything else stays per user.

### Access

Every `/admin` request needs all of these; anything short of the role gets a 404, so the area is not discoverable.
- Signed in, with the `Dev` role. `role grant|revoke dev --email ...` and `role list` in the CLI. The role is checked in the database on every admin request, so a revoke takes effect at once; a grant shows up in a live session within 5 minutes, when the sign-in cookie is refreshed, or at the next sign-in.
- A valid Cloudflare Access token. Cloudflare Access protects `plate.peychev.uk/admin*` (one-time email code, only the developer's address). The app still checks `Cf-Access-Jwt-Assertion` itself: RS256 against the team's certs (cached an hour, refetched for an unknown key id), `aud` equal to `Admin:CloudflareAccess:Audience`, `iss` equal to the team domain, expiry with one minute of skew, and the token's email equal to the account's. In Production the area is off until `Admin:CloudflareAccess:TeamDomain` and `Audience` are set; Development and Testing skip the check unless they are set.
- A passkey unlock: `/admin/unlock` asks for one of the account's own passkeys with user verification required, then sets `__Host-gym.admin` (HttpOnly, Secure, SameSite=Lax like the sign-in cookie, so arriving back from Cloudflare's login keeps the unlock; every admin write is a POST with an anti-forgery token) holding the user id, security stamp and unlock time. It lasts 30 minutes from the last admin request and 4 hours at most, and ends on Lock now, log out, Log out everywhere or a password change. A signed-in `Dev` account without it is sent to the unlock page. The unlock page is reached from Settings, so it follows the account's language (the admin pages themselves are English); its button reads "Unlock with a passkey", and an account with no passkey is told to add one in Settings, with a link to Sign-in & security, instead of a button that can only fail.

Admin responses are `Cache-Control: no-store`. When the Access session has expired, an admin page change falls back to a full page load (soft navigation already does this when a fetch fails), which shows Cloudflare's login; the eval progress poll reloads the page when a request fails.

### Pages

- Dashboard: the digest, server version, key status and models, and the unlock's expiry with Lock now.
- Reports: list with the CLI's filters; a report shows everything `feedback show` does with the screenshot inline, and sets status, reply and fixed-in version (defaulting to the running version). Opening a new report marks it Seen.
- Parses and errors as in the CLI; regression cases download as JSONL.
- What's new: list, add and remove.
- AI: the OpenRouter key (set, test, remove; only its last 4 characters are ever shown) and the parser, matcher, estimator, onboarding, weekly and label model pickers, with model accuracy for the parser, matcher and label models. Each chat task (parser, estimator, onboarding, weekly, label; not the matcher, whose decision models have none) has a reasoning effort setting on its model page: "Lowest" (the default, the model's lowest supported effort) or any effort the current model's catalog entry lists (`reasoning.supported_efforts`, without `none` when reasoning is mandatory). It is sent as `reasoning.effort` on every call for that task, the food namer taking the parser's; a pick the current model doesn't take (after a model change) is not sent, and the model's lowest goes instead. Saving runs the task's check with the new effort, and the overview shows the effort in use. For some models no reasoning costs more than a little (GPT 6.1 Terra), which is why the lowest isn't always the cheapest.
- Eval: the CLI's `eval` as a background job, one at a time, limit at most 20, with progress and the result kept in memory until the next job.
- Invites: open, redeemed, revoked and expired invites with note, created and expiry dates, and the account that redeemed each. Create takes a note and an expiry and shows the link once, with Copy and Share (the phone's share sheet). Revoke on an open invite. Section 9 has the rules.
- Labels: label scans, newest first, filterable to edited or failed ones; a scan shows its photos (while kept) beside the model's reading, the normalized values and what was saved. Like parse runs, this is feedback data, not a food log.
- Users: name, email, created date, role, passkey count, last report, and the note of the invite the account came from. Read-only; resetting a password and deleting an account stay in the CLI. The admin area shows what users sent as feedback and the parse and error records; it never browses another account's food log, weights or workouts.
- Audit: the latest entries.

### Audit and alerts

- `AdminAudit` rows record unlocks (and failed ones), invites created, revoked and redeemed, password resets, account deletions (with the deleted email in the detail), key and model changes, status and reply changes, changelog changes, eval runs, regression-case downloads and label photo downloads, from the web or the CLI, with the time, user, client IP and a short detail. Never a secret. Kept a year; `admin audit [--since 30d]` in the CLI.
- With Telegram configured, each unlock, each key change and each redeemed invite (its note and the new account's name) sends a message.

### CLI additions

- `role grant|revoke dev --email <email>`, `role list`.
- `ai show` (with each chat task's effort in use and the efforts its model takes), `ai set-model parser|matcher|onboarding|weekly|label|estimator <id>`, `ai set-effort parser|onboarding|weekly|label|estimator <effort>|lowest` (then runs that task's check), `ai set-key` (the key is read from standard input, never an argument), `ai clear-key`, `ai test [label]` (with `label`, reads a synthetic label photo bundled with the app and prints the values).
- `feedback changelog list|remove <id>`, `admin audit [--since 30d]`.
- `invite create [--note ...] [--expires 7d]` (prints the link), `invite list [--all]` (open ones by default), `invite revoke <id>`.
- `reset-password --email <email>`: prompts for the new password like `create-user`, clears any lockout, and updates the security stamp, so every session of that account signs out. Passkeys are kept.
- `delete-user --email <email>`: asks for the email again to confirm (or takes `--yes` when stdin is redirected), then deletes the account and every row it owns, feedback and screenshots included. Shared rows stay (foods added from a barcode, changelog entries); `Invite.RedeemedByUserId` is set null; a shared food it added stays and keeps the account's id in `AddedByUserId`, so others still see it as added by another user; and audit rows keep the user id. Refuses to delete the last `Dev` account. The user can export their data from Settings first; the nightly dumps still hold it for their 14-day retention.

---

## 13. Build phases and acceptance criteria

### Phase 1: Foundation and workout logging
- Solution structure, Docker setup, PostgreSQL, migrations, Identity with the create-user CLI.
- Exercise seed, templates CRUD, start and log sessions, history.
- Bodyweight entry on the Today page.
- Login hardening from section 9, the Cloudflare hostname, and the backup-script changes from section 10.
- **Done when:** every account can log a full workout from an iPhone at `plate.peychev.uk` over mobile data; the data lives in `~/docker/plate/db` and persists across `docker compose down` and `up`; the host script's dump restores into a fresh database; lockout and login rate limiting are tested; and user isolation tests pass.

### Phase 2: Progression and training analytics
- Progression service with suggestions in the active session view.
- e1RM, weekly volume, and their Progress charts. Session summary with PRs.
- Rest timer. Offline pending-set queue.
- **Done when:** all Core tests for 5.1 to 5.3 pass; logging sets with airplane mode toggled on and off, and with the app closed or the phone locked mid-request, results in no lost or duplicated sets; and the rest timer sound works on each user's iPhone.

### Phase 3: Nutrition
- FoodItem and FoodLogEntry, USDA subset import, text parsing via OpenRouter with database matching and confirmation, barcode scanning with its fallbacks, Open Food Facts lookup and caching, search, recents, copy meal.
- Daily totals on the Food and Today pages, using the prior maintenance estimate.
- **Done when:** a typical day can be logged in under two minutes total, and the app works with `FakeFoodParser` when no API key is set.

### Phase 4: Adaptive targets
- Trend weight, adaptive maintenance, daily targets with safety limits, Progress charts for weight and calories.
- **Done when:** all Core tests for 5.4 to 5.6 pass, including the synthetic convergence scenarios.

### Phase 5: Polish
- CSV export, PWA icons and manifest polish, README completion, backup restore repeated from a real nightly dump, `mem_limit` values set from Beszel readings.

### Phase 6: Feedback
- Section 11: parse runs and corrections, ratings, reports with screenshots and contextual snapshots, error capture, the offline queue, Telegram, What's new, model accuracy, and the CLI with its project skill.
- **Done when:** a rating, a report with a screenshot and a browser error each reach `feedback digest` over SSH from the Mac; a status and reply set from the CLI show on the reporter's phone; user isolation tests cover runs, reports and attachments; and a report sent in airplane mode arrives once when back online.

### Phase 7: Admin
- Section 12: the `Dev` role, Cloudflare Access check, passkey unlock, the admin pages, the audit log, AI settings moved out of Settings, and the CLI additions.
- **Done when:** an account without the Dev role sees read-only AI models and gets a 404 at `/admin`; the developer reaches `/admin` from the iPhone only through the Access code and Face ID; a status and reply set from the admin area show on the reporter's phone; an eval runs from the admin area; and the unlock shows in `admin audit` and on Telegram.

### Phase 8: Activity level and onboarding
- Activity level and the NASEM 2023 prior (5.5), profile limits in one place (5.7), the setup chat (6.4) with its form fallback, and the onboarding model in the admin area and CLI.
- **Done when:** the Core tests for 5.5 and 5.7 pass; a new account lands on the splash at first sign-in, finishes the chat in English and Bulgarian at 390 px, and Today shows the target computed from its answers; an existing account sees the chat once with its known values filled in; and the form works without an OpenRouter key.

### Phase 9: Macro and food-quality targets
- Macro targets with the reference weight and age rules (5.6), fiber and saturated fat stored per food and per entry with their sources (sections 4, 6.1 to 6.3), weekly nutrition with coverage (5.8), cut check-ins and diet breaks (5.9), and the screens in section 7.
- **Done when:** the Core tests for 5.6, 5.8 and 5.9 pass; the stored protein of 2.0 g/kg (the old default) is migrated to Auto and any other value kept; a USDA, an Open Food Facts, an AI and a quick entry each show fiber and saturated fat or count as unknown; and Today, Food, Settings, Progress and the setup card read correctly at 390 px in English and Bulgarian.

### Phase 10: Weekly summary
- Section 6.5: the stored summary, the facts it is written from, the weekly AI task in the admin area and CLI, the Settings switch, and the Today card.
- **Done when:** the Core tests for the enough-data rule, the week's range and the answer trimming pass; Today renders a stored summary without calling the model and loads a missing one without waiting; a failed model call shows nothing and is retried on a later visit; turning the switch off, or the model off, stops the calls; and the card reads correctly at 390 px in English and Bulgarian.

### Phase 11: QA fixes
- Every finding in `docs/qa/2026-10-05-ui-qa.md`: the workout screen (in-place set edits that save, stored set slots, rows pre-filled from the last set, server-side set numbers, the rest timer's Next and its place beside the Undo toast), the template editor and Settings saving on change, the app's own confirmation sheet, custom exercises and the multi-pick picker, reorder mode, scroll position kept after every change (except a form sent back with an error, which brings the error into view), taps during page transitions, food search ranking and live search, the fallback parser's matches and the "Change food" sheet, entry names without amounts, past-day targets and blocked future days, the totals card labels, chart series with one point, transition aborts kept out of the error log, the visual fixes (segment thumb, value and placeholder colours, scroll margins, light meters, theme switch, title on Back), the Bulgarian text, mixed-script food names, and the suggestions (Last time, Done today, plate bars).
- A browser test project (Playwright, Chromium and WebKit, 390 and 320 px) covers the behaviour that lives in the page.
- **Done when:** each finding has a test that failed before its fix and passes after; `dotnet test` (Core, Web and the browser tests) is green; a repeat of the QA pass in Chrome at 390 and 320 px, in English and Bulgarian, finds none of the listed problems; and the iOS checklist (wake lock, timer sound, Dynamic Type, safe areas, keyboard) passes on both iPhones.

### Phase 12: Invites
- The Invite entity (section 4), the invite rules and join page (section 9), the Invites admin page, and the CLI commands `invite`, `reset-password` and `delete-user` (section 12).
- **Done when:** an invite made in the CLI and one made in the admin area each create an account from an iPhone at 390 px, one in English and one in Bulgarian, and the new account lands on the onboarding splash; a used, revoked, expired or unknown token creates nothing and shows the same message; two concurrent redemptions of one token create one account; a taken email or a short password leaves the invite open; a sixth open invite is refused; the join limit returns 429 on the eleventh request; a third account passes the user isolation tests against the other two; `reset-password` signs out the account's sessions and the new password works; `delete-user` leaves no row owned by the account and refuses the last `Dev`; and the redemption shows in `admin audit` and on Telegram.

### Phase 13: Label photos and shared foods
- The Text composer's first line level with the send button (section 7), in the food composer and the setup chat.
- `FoodItem.AddedByUserId`, the split barcode index, own-first barcode lookup, the share checkbox and the "Added by another user" mark (sections 4 and 7).
- Label reading (6.6): the label AI task and its picker, `ILabelReader` and `LabelValues` in Core, `OpenRouterLabelReader`, the photo flow on the manual form and the Scan tab, `LabelScan` and its photo with their retention, the Labels admin page, label accuracy, and the CLI additions (sections 11 and 12).
- **Done when:** the Core tests for every 6.6 normalizing case pass; a browser test shows the composer's text centred on the send button within 1 px in Chromium and WebKit at 390 px, in English and Bulgarian, here and in the setup chat; on an iPhone at 390 px, in English and Bulgarian, a photo of a real Bulgarian label (camera and photo library both) fills the form after a barcode miss, and the saved shared food is found by another account's scan with "Added by another user" and no name; an unticked one stays private and another account can save its own for the same barcode; a per-serving label converts and a non-label photo shows the not-found message; the photo is gone after 30 days and `delete-user` leaves no scan; a third account passes the user isolation tests for scans and photos; the twenty-first label read in 10 minutes returns 429; the label model's accuracy shows in the admin area; and `ai test label` reads the bundled photo.

### Phase 14: Settings hub
- Section 7 Settings: the hub of rows with their current values, one page each for Profile, Goal & nutrition, Training, General and Sign-in & security, per-page saves, and no AI models section.
- **Done when:** saving any page leaves every other page's fields as they were; each page opens from the hub and Back returns to it, with no sideways scroll, at 320 and 390 px in English and Bulgarian in Chromium and WebKit; and no account sees the AI models in Settings.

### Phase 15: Barcode field scan and more label photos
- The scan button beside the manual form's barcode field, the "already in the app" card and its reject rules (section 7).
- Up to three photos per label read: the optional slots behind "Add more photos", one read for all of them, `LabelScanPhoto.Position`, the 7 MB request limit, and the admin page and `label-photo <id> [n]` for each photo (sections 4, 6.6, 9, 11, 12).
- **Done when:** on an iPhone at 390 px, in English and Bulgarian, the scan button fills the barcode field on the Manual tab and in the form opened by "Photograph a label", with the camera and with "Take a photo instead", without leaving the page; a code the account already saved shows the card and "Yes, use it" opens its grams screen; rejecting a shared match saves a private food; an account without a label model sees the share row appear when a barcode is typed; a single photo reads at once and "Add more photos" appears only in the form it filled, with that photo in the first slot; a table photo and a front-of-pack photo read together fill the name and serving the table lacked; the admin page shows all three photos and `label-photo <id> 3` prints the third; and the twenty-first read in 10 minutes still returns 429.

### Phase 16: Log it again faster
- Repeat logging (5.10) in Core: the last amount, the Recent order and the usual food key.
- The last amount on the barcode result, the search results and parsed items without an amount; "Add and scan another" on the barcode result; Recent in the 5.10 order (section 7).
- `UsualFood` (section 4), `AmountGiven` in the parser's schema and prompt, the usual-food lookup and writing, and the "Your usual" marks (6.1); the usual flag in the run's `Resolved` and `feedback parses`, retention, export and account deletion (section 11).
- **Done when:** the Core tests for 5.10 pass; on an iPhone at 390 px, in English and Bulgarian, a product logged at 250 g offers 250 g with "Last time" on its next scan and in search, and a product never logged offers its serving; three products are logged into one meal with "Add and scan another" without passing through the Food day; Recent at Breakfast lists what the account eats at breakfast first; after confirming "200г извара" with a hand-picked food, "извара" proposes that food marked "Your usual" with 200 g, and "300г извара" keeps 300 g; picking another food once moves "извара" to it; all of this works with `FakeFoodParser` and no key; `feedback parses` marks the usual item; and the user isolation tests show that another account's entries never set a last amount or a Recent order and that a usual food pointing at another account's private food is ignored.

### Phase 18: Recipes
- `Recipe` and `RecipeIngredient` (section 4), `RecipeMath` (5.12), the recipe page and the Recipes group on the Recent tab (section 7), recipes in search, Recent and parsing, export and account deletion.
- **Done when:** the Core tests for 5.12 pass; on an iPhone at 390 px, in English and Bulgarian, a recipe typed as text with a cooked weight saves, "мусака 350г" parses to it, and logging it from Recent adds its values; editing it leaves yesterday's entry unchanged; it works without a key; and the user isolation tests cover recipes.

### Later
Ideas kept for after Phase 9, not planned yet. The nutrition ideas should not start before real logs show fiber coverage is good.
- A monthly cap on AI calls per account, once a third account uses the app regularly. The per-user rate limits in section 9 bound bursts but not a month's total cost.
- Sending shared manual foods to Open Food Facts, so other apps get them too. Needs an OFF account and their write API.
- Changing whether a manual food is shared after it is saved.
- Protein timing around training: the app knows when sessions happen, so it could suggest protein in the meal before or after.
- Fruit and vegetables in grams, against WHO's 400 g a day.
- Sodium and free sugars (Open Food Facts has both; USDA has most), against WHO's under 2 g sodium and under 10% of energy from free sugars.
- Alcohol as its own energy line, since it is neither a macro the targets account for nor visible in the totals.
- A weekly diet quality score built from protein, fiber, saturated fat and fruit and vegetables.
