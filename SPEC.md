# GymTracker: Product and Technical Specification

Self-hosted training and nutrition tracker for two users (me and my father). Runs in Docker on the headless Ubuntu home server "wizard", used from iPhones as an installed PWA, reached remotely at `https://gym.peychev.uk` through the server's existing Cloudflare Tunnel. Replaces ad hoc calorie tracking with a system that logs workouts, suggests progression, parses food entries with an LLM, and estimates real maintenance calories from logged data.

Build in the phases at the end of this document. Each phase must be usable on its own before the next starts.

---

## 1. Goals and non-goals

**Goals**
- Logging a set at the gym takes one or two taps.
- The app tells me what weight and reps to aim for on every exercise.
- Food logging accepts plain text ("200g chicken breast, 150g cooked rice, a banana") and barcodes.
- Calorie targets adapt to my actual weight trend instead of a fixed guess.
- Two fully separate accounts with no data leakage between them.

**Non-goals**
- Public sign-up, social features, sharing.
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
| LLM | OpenRouter chat completions API (`openai/gpt-6-luna` by default, model picked in the admin area) via typed HttpClient, behind an interface. It extracts foods and grams; nutrition values come from the food database (section 6.1) |
| Food matching | Optionally TypeSafe's Jev decision model (`typesafe/jev-1.13`) through OpenRouter's Decisions API, chosen in the admin area: it picks which shortlisted database food each parsed item is, or that none fits (section 6.1) |
| Food database | Users' own and shared `FoodItem`s, a locally imported subset of USDA FoodData Central for generic foods, and Open Food Facts API v3 for barcodes |
| Barcode scanning | `barcode-detector` ponyfill (ZXing-C++ compiled to WASM), self-hosted. Safari on iOS ships the native `BarcodeDetector` API disabled, and `@zxing/browser` sits on a library in maintenance mode |
| Tests | xUnit, FluentAssertions; Testcontainers (`postgres:18`) for PostgreSQL integration tests |
| Deployment | docker-compose on wizard; HTTPS via the existing Cloudflare Tunnel (`gym.peychev.uk`) |
| Runtime image | `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra` (includes ICU and time zone data for `Europe/Sofia` and Bulgarian text). No Native AOT (unsupported for MVC and cookie auth) |

Server-rendered MVC with htmx is chosen over Blazor Server deliberately: gym signal drops would kill Blazor Server circuits mid-workout, while plain HTTP requests can simply be retried.

.NET 10 is LTS, supported until 2028-11-14. PostgreSQL 18 is supported until 2030-11-14.

---

## 3. Solution structure

```
GymTracker.sln
src/
  GymTracker.Core/            Domain entities, enums, pure calculation services. No EF, no HTTP, no ASP.NET references.
  GymTracker.Web/             Controllers, views, view models, htmx partials, static assets, PWA manifest and service worker.
    Data/                     EF DbContext, migrations, seed data (exercises, USDA subset).
    Integrations/             OpenRouter client and model catalog, Jev match decider, Open Food Facts client.
    Features/                 Thin feature services that use the DbContext directly and call Core for the math.
tests/
  GymTracker.Core.Tests/      Unit tests for all calculation logic.
  GymTracker.Web.Tests/       Integration tests against real PostgreSQL via Testcontainers, including user-isolation tests.
docker/
  Dockerfile
  docker-compose.yml
  docker-compose.dev.yml
```

All business math (progression, e1RM, volume, trend weight, maintenance estimate, targets) lives in `GymTracker.Core` as pure, deterministic functions that take data in and return results. Controllers never contain calculations.

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
- `Id`, `OwnerUserId` (nullable for shared items: the Open Food Facts cache, the USDA subset, and manual items created for a barcode)
- `Name`, `NameBg` (nullable, Bulgarian name), `Brand` (nullable), `Barcode` (nullable, unique where not null)
- `KcalPer100g`, `ProteinPer100g`, `CarbsPer100g`, `FatPer100g`
- `FiberPer100g`, `SaturatedFatPer100g` (nullable; null means unknown, not zero)
- `DefaultServingGrams` (nullable)
- `Source` (OpenFoodFacts, Usda, Manual, Ai)
- Manual items created from a barcode scan are shared (`OwnerUserId` null) so the other user doesn't re-enter the same product; Open Food Facts coverage of Bulgarian products is thin, so this path will be common. Manual items without a barcode stay private.

**SavedMeal**
- `Id`, `UserId`, `Name` (up to 60 characters), `CreatedAt`

**SavedMealItem**
- `Id`, `SavedMealId`, `SortOrder`, `Description`, `Grams` (0 for a quick entry), stored `Kcal`, `ProteinG`, `CarbsG`, `FatG`, `FiberG` and `SaturatedFatG` (both nullable), `FoodItemId` (nullable), `Source`
- A snapshot of a logged entry, so later food edits don't change the saved meal.

**FoodLogEntry**
- `Id`, `UserId`, `Date`, `Meal` (Breakfast, Lunch, Dinner, Snack)
- `Description` (what is shown in the log, e.g. "Chicken breast, grilled")
- `Grams`
- `Kcal`, `ProteinG`, `CarbsG`, `FatG` (stored totals, so the entry is stable even if the FoodItem changes)
- `FiberG`, `SaturatedFatG` (nullable stored totals; null when the food has no value)
- `FoodItemId` (nullable), `Source` (Barcode, Search, Ai, Manual, Recent, Quick). A Quick entry is calories and macros typed directly: no food, `Grams` 0, edited by its numbers and never scaled.
- `AiModel` (nullable; the OpenRouter model id for AI-parsed entries, so a shift in estimates after a model change can be traced)
- `MatchModel` (nullable; the decision model version that chose the matched food or decided there is none, for the same reason; null when the database ranking decided)
- `CreatedAt`

**MaintenanceEstimate**
- `Id`, `UserId`, `CalculatedOn` (DateOnly)
- `EstimatedTdeeKcal`, `TargetKcal`, `TargetProteinG`
- `TrendWeightKg`, `DaysOfData`, `LoggingCompleteness` (0 to 1), `Method` (Initial, Adaptive)

**WeeklySummary** (6.5)
- `Id`, `UserId`, `WeekStart` (the Monday of the week it describes; unique per user and week)
- `Summary` (up to 400 characters), `Tips` (up to three, each up to 160), `Motivation` (up to 160)
- `Language` (the account's language when written), `Model`, `CreatedAt`

---

## 5. Core calculations (GymTracker.Core)

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

---

## 6. External integrations (GymTracker.Web/Integrations)

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
- `Assumption` (string such as "assumed one medium banana, 120 g")
- `EstimatedKcalPer100g`, `EstimatedProteinPer100g`, `EstimatedCarbsPer100g`, `EstimatedFatPer100g`, `EstimatedFiberPer100g`, `EstimatedSaturatedFatPer100g`: the model's own values, used only as a fallback

Implementation `OpenRouterFoodParser`:
- Typed `HttpClient` calling OpenRouter's `POST /api/v1/chat/completions`.
- Model: each AI task has its own model setting, shared by both accounts like the key (the parser, the matcher below, the setup chat in 6.4 and the weekly summary in 6.5). The parser's is picked from OpenRouter's catalog (`GET /api/v1/models`, cached for an hour, limited to text models that support `structured_outputs` and `response_format`, `:batch` variants excluded), otherwise configuration `OpenRouter:Model`, otherwise `openai/gpt-6-luna`; a saved id must be in the catalog.
- API key from Settings (encrypted with Data Protection) or configuration `OpenRouter:ApiKey` (environment variable `OPENROUTER_API_KEY` in Docker). Never commit it.
- Request structured output: `response_format: {type: "json_schema", json_schema: {name, strict: true, schema}}` with a strict-compatible schema (`additionalProperties: false`, every property required, nullable ones typed `["string","null"]`) matching `ParsedFoodItem[]`, and `provider: {require_parameters: true}`. Because that excludes providers lacking any sent parameter, `temperature` and `reasoning.effort` (the model's lowest supported effort) are sent only when the catalog lists them for the model.
- System prompt: the user writes foods in English or Bulgarian; return one item per food; estimate grams for vague quantities ("a banana", "a plate of rice") and state the assumption; assume raw weight for meat and cooked weight for rice and pasta unless stated; always fill `NameEn`.
- Timeout 15 seconds, one retry on 5xx or timeout. On failure, the UI falls back to manual entry with the text preserved.

Resolving nutrition values (a Web feature service, not the parser):
1. Match `NameEn`/`NameOriginal` and `State` against the user's own `FoodItem`s and recent entries, then shared items including the USDA subset (PostgreSQL `pg_trgm` similarity on names). A ranking adds bonuses for the user's own foods, the food's main word, the right raw or cooked state, and fewer niche variants, and its top candidate is the match.
2. When a matcher model is chosen in Settings and a key exists, a decision model chooses instead (below). Otherwise, and whenever it gives no usable answer, the ranking's top candidate stands.
3. If a match is found, compute totals from its per-100 g values and show the matched item's name, with a control to pick a different match.
4. If not, use the model's estimated values and mark the item Low confidence. If `|Kcal - (4*Protein + 4*Carbs + 9*Fat)|` exceeds 15% of `Kcal` for those values, also say so.
5. Reject negative values or grams above 3,000.

Matcher (`IFoodMatchDecider`, interface in Core; `JevFoodMatchDecider` in Integrations):
- Settings offers Off (the ranking alone, the default) and `typesafe/jev-1.13`, pinned: Jev is not in `/api/v1/models`, and its thresholds were measured on that version, so a new version is evaluated before it is offered.
- Per item, a shortlist of the plain trigram top 50 (similarity of at least 0.3 on `NameEn`, and on `NameOriginal` when it differs, merged by the better similarity, best first; the ranking is not used). Jev leans toward the first option, so the order is fixed.
- One `POST https://openrouter.ai/api/alpha/decisions` per parse with the same key. State: the typed text and every item (as written, English name, state, preparation, grams). One Choice question per item that has candidates; each option names the food in English and Bulgarian, its brand, and kcal and protein per 100 g, plus a `none` option.
- When the ranking's top candidate is one of the user's own foods, it stands: that item is sent as context only, not as a question.
- A picked food is the match: High confidence when Jev's confidence is 0.95 or more, otherwise Medium. `none` at confidence 0.9 or more is no match (step 4). A `none` below 0.9, an option that was not offered, or a missing answer leaves that item to the ranking.
- 3-second timeout, no retry; any error, timeout or unreadable response falls back to the ranking silently. One log line per call with the version, latency, input tokens and cost (input tokens only, about $0.0002 per parse).
- Jev's main training language is English. It was evaluated on Bulgarian and English entries before adoption (gated rule: 29 of 32 held-out items against the ranking's 24), but its Bulgarian accuracy is the thing to watch.

Results are never saved directly. The user always sees an editable confirmation list first. Saved entries record `AiModel`.

Also provide `FakeFoodParser` for tests and local development without a key (selected when no OpenRouter key is set).

### 6.2 Open Food Facts

`IFoodDatabase` with `GetByBarcodeAsync(string barcode)` and `SearchAsync(string query)`.
- Use API v3 (v2 is deprecated): `GET https://world.openfoodfacts.org/api/v3/product/{barcode}` with a `fields` parameter limited to name, brands, nutriments and serving quantity. Confirm the v3 field names against the current OFF docs before coding.
- Read `energy-kcal_100g`, `proteins_100g`, `carbohydrates_100g`, `fat_100g`. If kcal is missing but kJ is present, convert by dividing by 4.184.
- Also read `fiber_100g` and `saturated-fat_100g` when present. They are optional: a product without them still counts as having nutrition data.
- Set the `User-Agent` OFF requires: `GymTracker/<version> (<contact email from config>)`.
- Rate limits are 15 product reads and 10 searches per minute per IP, shared by both users because all calls leave from wizard. Call OFF search only on explicit submit (never as-you-type), and treat HTTP 429 as "try again shortly" without breaking the page.
- Cache every successful lookup as a shared `FoodItem` so repeat scans are instant and work offline from the API.
- If the product is missing or has no nutrition data, offer to create a manual `FoodItem` with that barcode (shared, see section 4).

### 6.3 USDA FoodData Central subset

Generic foods (chicken breast, cooked rice, banana) come from USDA FoodData Central, which is public domain (CC0).
- A one-off script, committed to the repository, converts the Foundation and SR Legacy downloads into a trimmed CSV (name, kcal, protein, carbs, fat, fiber and saturated fat per 100 g, the last two blank where USDA has no value, raw/cooked where the name states it) under `src/GymTracker.Web/Data/Seed/`.
- On startup, if no `Usda` items exist, the app imports the CSV as shared `FoodItem`s with `Source = Usda`. No runtime calls to the USDA API.
- Databases seeded before the CSV had fiber and saturated fat get them once, matched by English name, the way Bulgarian names were added. Rows that already have a value are left alone.
- The Bulgarian foods CSV (`bg-foods.csv`) has the same two optional columns. Products get them from Open Food Facts by barcode (`tools/foods/add-fiber.mjs`), and existing databases get them on the next start where both are still unknown. Dishes stay blank, since their source pages don't give them.

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

**Model call.** `IWeeklySummaryWriter` in Core, `OpenRouterWeeklySummaryWriter` in Integrations, with the same request shape, structured output, timeout, retry and error handling as the setup chat (6.4). Schema: `summary` (string), `tips` (array of strings), `motivation` (string). The answer is trimmed to the lengths above, empty tips are dropped, and an empty summary counts as a failure. A fourth AI task, `weekly`, has its own model setting; unset, it uses the parser's model, and off turns the summary off for both accounts.

**Prompt rules.** Plain and warm, in the account's language, using the informal "ти" in Bulgarian. Mention real numbers from the facts and never invent one. Tips are specific actions for next week drawn from the weakest numbers (for example protein at breakfast when the minimum was missed); when a number has no target or too little data, leave it out. No medical advice, no comments on the user's body or appearance, no praise or blame for weight changes; describe the trend against the goal neutrally.

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
- Date selector (defaults to today), entries grouped by meal, daily totals at the top. Future days can't be opened or logged. A past day shows the targets in force that day (the maintenance estimate of that day, 5.5, with the profile's goal rules, 5.6); days before the first estimate say when targets start.
- The totals card shows each macro against its target (5.6): protein against the aim with the minimum, fat and carbs against their ranges, fiber against its minimum and saturated fat against its maximum, with the coverage note when a day is under 80% (5.8). Each of Breakfast, Lunch and Dinner shows its protein, with a check once it reaches the per-meal aim.
- A This week card with the 5.8 averages.
- Add entry with four methods:
  1. **Text:** a textarea; submit sends to the parser and shows the editable confirmation list; confirm saves all items.
  2. **Barcode:** opens the camera scanner (EAN-13, EAN-8, UPC-A, UPC-E only); on detection, looks up the product and asks for grams (pre-filled with the serving size). Installed iOS web apps do not keep camera permission, so expect a prompt on each launch, and open the scanner without changing the URL (no `hx-push-url`), since route changes can re-prompt or stop the stream. Fallbacks on the same screen: a field to type the digits, and a "take photo" button (`<input type="file" accept="image/*" capture="environment">`) decoded with the same detector.
  3. **Search:** searches as you type: the user's own FoodItems and recent entries first, then shared items (including the USDA subset), then Open Food Facts on explicit submit. Names that start with the search (singular or plural) rank above names that merely contain it, and identical foods are listed once.
  4. **Recent / favourites:** one-tap re-log of the 20 most frequent entries from the last 30 days, with editable grams.
- Edit or delete any entry (open it, or swipe the row). Copy yesterday's meal into today, offered only when yesterday has entries for that meal. An entry's name never carries its amount; the grams are shown beside it.
- On the confirmation list, each item can be switched to another food from a search, and a weak database match is marked "Check this" rather than "Database".
- Quick add per meal: a name (optional), calories, and optional protein, carbs, fat, fiber and saturated fat, for restaurant meals and labels not worth saving. Quick entries stay out of Recent.
- Saved meals: a meal's menu on the Food day saves its entries under a name. Saved meals are listed first on the Recent tab; tapping one opens a sheet with editable grams per item (quick items keep their numbers) and logs them all into the chosen meal. Rename or delete from the same sheet.
- Deleting a food entry or a set, and removing an exercise from a session or a template, happen at once with an Undo toast for 6 seconds (no confirm). Archiving a template, finishing or deleting a workout, removing a passkey, signing out everywhere and deleting a saved meal still ask first, in the app's own confirmation sheet (never the browser's `confirm`). The Undo toast belongs to the page it was shown on and goes away on another page; its timer pauses while it is pressed, and a failed undo says so.

### Progress
- Bodyweight chart: raw weigh-ins as dots, trend weight as a line, selectable range (4 weeks, 12 weeks, all).
- Per-exercise chart of best e1RM per session, with an exercise picker.
- Weekly volume per muscle group as a bar chart for the current and previous week.
- Calories: daily intake versus target for the last 4 weeks, with the maintenance estimate as a line.
- Protein: daily intake against the minimum and aim for the same 4 weeks, and the This week card (5.8).

### Settings
- Every setting saves when it changes, with a "Saved" mark or an inline error beside it; there is no Save button except for the password.
- Profile fields from section 4, with the limits in 5.7. Inputs take their `min` and `max` from the same limits. Weight jump and bar and plates are grouped under Training; the time zone is picked from a list.
- Protein: Auto (by goal) or a custom g/kg. Under it, the current aim in grams, and when the reference weight applies, "Based on {0} kg, a healthy weight for your height." A "How your targets are set" sheet gives each 5.6 rule in one line with its source.
- Activity level, one line of explanation per choice. The levels follow NASEM 2023 table 7-1, which defines them as activity on top of daily living (about 30 minutes of walking): low active adds 60 to 80 minutes of brisk walking a day, active about two hours including some vigorous activity, very active more than two hours of vigorous activity. The step counts are a rough conversion.
  - Inactive: desk job and everyday errands, up to about 8,000 steps a day.
  - Low active: everyday life plus about an hour of brisk walking or similar every day, roughly 11,000 to 14,000 steps.
  - Active: everyday life plus about two hours of activity a day, some of it hard (cycling, a sport), or a job on your feet with lifting and carrying.
  - Very active: more than two hours of hard activity a day (running, cycling, sport), or heavy manual work plus training.
  Under it: "Lifting 3 to 4 times a week counts for about half a level; add it to your walking. Used only until about three weeks of logs give an adaptive estimate." (An hour of lifting is about 3.5 to 5 MET, so 200 to 320 kcal above rest for 80 kg; four a week is 115 to 185 kcal a day against about 220 kcal between inactive and low active.)
- Run the setup chat again (section 6.4).
- Custom exercises: create (name, primary muscle, equipment, optional secondary muscles), rename and archive the user's own exercises. They appear in the picker with the shared library and are never visible to the other account.
- Bar and plates: the profile bar weight and which plates the gym has.
- Weekly AI summary: on or off (6.5), with the line "Last week's numbers are sent to an AI model through OpenRouter."
- AI models: the current food parser, food matcher, setup chat and weekly summary models, read-only. Only the developer changes the key and models, in the admin area (section 12).
- Admin: a row shown only to accounts with the `Dev` role, linking to `/admin`.
- Export all of the user's data as CSV files in a zip.
- Feedback: Send feedback and Your feedback (section 11).
- Change password, log out.

---

## 8. PWA and resilience

- Web app manifest with name, icons (192 and 512), `display: standalone`, dark theme colour, start URL `/today`.
- Service worker that precaches static assets (CSS, JS including the vendored libraries and the scanner `.wasm`, icons) and one offline fallback page. Static files carry a content-hash query string, so the worker serves a cached copy of the exact URL without revalidating ("No connection. Sets you log are saved on the phone and will send when you're back online."). Pages and data are otherwise always fetched from the server.
- **Set logging must survive bad gym signal.** iOS has no Background Sync, so the page drives all retries. The queue is write-ahead:
  - When the check button is tapped, generate the `ClientId` Guid, write the set (including `CompletedAt`) to the pending queue in `localStorage`, render it as "pending", and only then send it. A set is never only in flight; this way it survives the app being closed or suspended mid-request.
  - Sending uses plain `fetch` in a small module, not htmx. Read the anti-forgery token at send time rather than storing it with the set.
  - On success (including a duplicate `ClientId`), remove the set from the queue and replace the pending row with the server's rendering.
  - Retry while the queue is non-empty: on the `online` event, on `visibilitychange` to visible, on `pageshow`, at app start, after every successful request, and every 15 seconds. Only one send in flight at a time.
  - On a validation error (4xx other than 401), stop retrying that set and show the error on its row.
  - On 401, keep the queue and send the user to the login page; the queue resumes after login.
  - When the active session page loads, merge any pending sets for that session back into the view.
  - Call `navigator.storage.persist()` once in standalone mode to reduce the chance of eviction.
  - The server treats a repeated `ClientId` as success without creating a duplicate, after checking the existing set belongs to the current user, and returns the same response as for a new insert.
- The active session must be fully restorable after the app is closed or the phone locks: all state lives on the server, except the pending queue, values typed into rows not logged yet, and the rest timer end time (all in `localStorage`).
- Numbers are written with a dot in both languages. The server also accepts a typed comma, since the Bulgarian iPhone keypad has one.

---

## 9. Security

- Public registration is disabled. Accounts are created with a CLI command: `dotnet GymTracker.Web.dll create-user --email ... --name ...`, which prompts for the password.
- Every controller action requires authentication except login.
- Global EF query filters plus explicit ownership checks on every write; integration tests must verify that user A cannot read or modify user B's sessions, sets, food entries or weigh-ins by ID.
- Anti-forgery tokens on all POSTs, including htmx requests (send the token via `hx-headers`) and the set queue's `fetch` calls.
- Secrets only from environment variables, except the OpenRouter key, which the developer may also save from the admin area (encrypted with Data Protection). Provide `.env.example`; `.env` is in `.gitignore`.
- Roles: one Identity role, `Dev`, granted and revoked only with the CLI over SSH (section 12). No web page grants a role or creates an account.

**Exposure.** The app is reachable at `https://gym.peychev.uk` through wizard's Cloudflare Tunnel, with no Cloudflare Access in front of it (decided 2026-10-02). Access would add a second login with an emailed code about monthly, and an expired Access session turns htmx and queue requests into failed cross-origin redirects. The app's own login is therefore the only gate and must be hardened:
- Lockout on: `PasswordSignInAsync(..., lockoutOnFailure: true)` (the template default is `false`), 5 failed attempts, 15-minute lockout.
- ASP.NET Core rate limiting middleware on the login POST, partitioned by client IP.
- Forwarded headers: behind cloudflared every request comes from 172.30.0.10, so configure `ForwardedHeadersOptions` with that address as the only known proxy, read the client IP from `CF-Connecting-IP` and the scheme from `X-Forwarded-Proto`. Without this, rate limiting puts every client in one bucket.
- Passwords: minimum 12 characters, no composition rules. An unknown email costs one password hash like a known one, so timing does not reveal which accounts exist.
- A second, global login limit across all addresses (30 attempts per 15 minutes). Per-user limits on password and API key changes (5 per 15 minutes), admin unlock requests (10 per 15 minutes, two per unlock), AI food parsing (30 per 10 minutes), setup chat messages (40 per 10 minutes), weekly summaries (6 per hour) and admin writes (60 per 10 minutes).
- Passkeys alongside passwords: registered in Settings, usernameless sign-in on the login page (button and autofill), relying party `gym.peychev.uk`.
- Response headers: Content-Security-Policy (`script-src 'self' 'wasm-unsafe-eval'`, no inline scripts or event handlers; sent report-only with a `/csp-report` log until `CSP_ENFORCE=true`), HSTS, `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: same-origin`, `Permissions-Policy` (camera only), COOP. `AllowedHosts` limited to the public host name. Request bodies over 1 MB are refused.
- Cookie: `Secure`, `HttpOnly`, `SameSite=Lax`, `__Host-` prefix, sliding 30-day expiration. "Log out everywhere" in Settings updates the security stamp.
- Data Protection keys persisted to a bind-mounted directory, so rebuilding the container does not log both users out.
- The container publishes no host port. cloudflared reaches it by container name on the external `tunnel` Docker network. An optional LAN-only port may be added for testing (pick one that is free on wizard; 8080 is AdGuard's).

If Cloudflare Access is ever added in front of the whole app (it covers only `/admin*`, section 12), the spec must also require: a one-month Access session, `X-Requested-With: XMLHttpRequest` on every htmx and queue request (so Access returns 401 instead of redirecting), 401 handled as in section 8, and a Bypass policy for the manifest, icons and static assets.

---

## 10. Deployment

The stack lives at `~/docker/gymtracker` on wizard, like the other stacks. wizard's boot SSD is failing and Docker named volumes live on it outside the nightly backup, so all state uses bind mounts under the stack folder.

**docker-compose.yml** with:
- `db`: `postgres:18`, container name `gymtracker-db`, bind mount `./db:/var/lib/postgresql` (the 18 image moved its data directory to `/var/lib/postgresql/18/docker`; mounting the old `/var/lib/postgresql/data` path can silently put the data in an anonymous volume). Small-footprint settings: `shared_buffers=32MB`, `max_connections=15`, `jit=off`. `mem_limit` (start at 256m, adjust from Beszel readings). Healthcheck with `pg_isready`. Default network only.
- `app`: built from `docker/Dockerfile` (multi-stage build on the .NET 10 SDK image, runtime on `aspnet:10.0-noble-chiseled-extra`), container name `gymtracker-app`, depends on a healthy `db`, no published ports, joined to the default network and the external `tunnel` network, bind mount `./keys` for Data Protection keys, environment from `.env`, `mem_limit: 256m` (adjust from Beszel readings), `cpus: "1.0"`, `restart: unless-stopped`. Workstation (non-concurrent) GC and a ReadyToRun publish for `linux-x64`; connection pool capped at 8. The container runs read-only (tmpfs `/tmp`), with all capabilities dropped, `no-new-privileges` and a PID limit. Base images are pinned by digest and refreshed monthly; the build fails on high or critical NuGet advisories.
- No backup container (see below).

**docker-compose.dev.yml** for local development on a Mac: only `db` (`postgres:18`), published on `127.0.0.1:5432`, no `tunnel` network (it exists only on wizard).

The app applies EF migrations automatically on startup (including `CREATE EXTENSION pg_trgm`), adds any seed exercise the shared library lacks (matched by name) and imports the USDA subset if absent.

**Backups** go through wizard's existing nightly host script `/usr/local/bin/homeserver-backup` (03:30), which already writes dated snapshots to the HDD with 14-day retention, Telegram alerts on failure and the PC pull. Two changes to it, which need sudo and are run by Antoan:
- Add a dump step, e.g. `docker exec gymtracker-db pg_dump -Fc -U gym gym > "$DEST/gymtracker-db.dump"`.
- Exclude `gymtracker/db` from the `~/docker` rsync, the same way `nextcloud/db_data` is excluded (a file copy of a running database is not a consistent backup).

Building the image on wizard pulls the .NET SDK image onto the failing SSD; prune the build cache after builds (`docker builder prune`), or build elsewhere for `linux/amd64`.

HTTPS (required for the camera and PWA install): add the public hostname `gym.peychev.uk` → `http://gymtracker-app:8080` to the "wizard" tunnel in the Cloudflare Zero Trust dashboard. No Access application on it (section 9).

**README** must cover: prerequisites, `.env` setup, `docker compose up -d`, creating users, the Cloudflare public hostname, the two backup-script changes, restoring from a dump (`pg_restore` into a fresh database), and installing the PWA on iPhone (Safari, Share, Add to Home Screen).

---

## 11. Feedback

Both users report problems and ideas from their phones, food parsing collects ratings and the corrections people make, and errors are recorded on their own. the agent reads all of it over SSH with a CLI command and writes back a status and a short reply. Nothing is exposed on the public host beyond the app's own authenticated pages.

### Data

- **ParseRun** (user-owned): one row per text parse. `Text`, `AiModel`, `MatchModel`, `ParserMs`, `MatchMs`, `ParserError`, `Parsed` (jsonb, the parser's items as returned), `Resolved` (jsonb, per item: the chosen food, confidence, notes, the ranking's top 5 candidates with scores, and Jev's raw answer when it ran), `Saved` (jsonb, what the user saved per item, null until confirmed), `Corrections` (flags), `ConfirmedAt`. `FoodLogEntry.ParseRunId` (nullable, set null on delete) links saved entries to their run.
- **Feedback** (user-owned): `ClientId` (Guid, unique, for the offline queue), `Source` (Settings, ParseRating, Barcode, Progression, Maintenance, FoodEntry, ErrorPage), `Kind` (Problem, Idea, Other), `Area`, `Rating` (Up, Down; parse ratings only), `Reasons` (flags: wrong food, wrong amount, wrong nutrition values, missed a food, extra item, should be split or merged, language not understood), `Message` (up to 2,000 characters), `Context` (jsonb: page, app version, user agent, viewport, standalone, language, theme, local time, recent client errors, and a snapshot of what the report is about), `ParseRunId` and `ParseItemIndex`, `ErrorEventId`, `Status` (New, Seen, Planned, Fixed, WontFix, NeedsInfo), `Reply` (up to 1,000), `StatusChangedAt`, `FixedInVersion`, `ReplySeenAt`.
- **FeedbackAttachment**: one image per report (`ContentType`, `Width`, `Height`, `Bytes`), in its own table so lists never load images. Stored in PostgreSQL so the nightly dump includes it.
- **ErrorEvent** (not user-facing, no user filter): unhandled server exceptions and browser errors, one row per fingerprint (type, message with numbers and ids removed, first in-app stack frame, path template) with `Count`, first and last seen, last trace id, first and last app version.
- **ChangelogEntry** (shared): `TextEn`, `TextBg`, `FeedbackId` (nullable), `Version`. `UserProfile.ChangelogSeenAt`.
- Areas: Training (workout logging, progression suggestions, rest timer, templates, plate calculator), Food (text parsing, food matching, barcode scanning, food search, nutrition values, saved meals), Targets and progress (calories and maintenance, progress charts), App (offline and sync, sign-in and passkeys, Bulgarian wording, layout and design, speed, other).

### Food parsing

- Every parse stores a ParseRun. Confirming stores what was saved and compares it with what was suggested (`ParseCorrections` in Core): an item unticked, grams changed by more than 10% and more than 10 g, any per-100 g value edited, the description edited, a different food. This needs no tap. A missing or foreign run id never blocks saving food.
- The Check foods screen asks "Did this read your food right?" with thumbs up and down, and each item has its own thumbs down. A tap records at once without submitting the form; a thumbs down then opens a sheet with the reasons and an optional comment. An AI entry's page offers "Report a parsing problem" for the same run.

### Reporting

- Settings has Send feedback (kind, area, message, an optional screenshot) and Your feedback (own reports, newest first, with status and reply; a badge when a reply is unread).
- Contextual links open the same form with a snapshot of what was shown: a barcode result (the code and the food's per-100 g values), a progression suggestion (the inputs the Core service received and its output) and the maintenance estimate (the stored estimate and the window's daily intake and weigh-ins). A referenced row is loaded through the user's query filters, so a foreign id is not found.
- Screenshots are downscaled on the phone to at most 1,600 px and JPEG quality 0.8; the server accepts JPEG, PNG or WebP by their first bytes, up to 900 KB.
- Reports and ratings are written to a `localStorage` queue first and sent with the same retry rules as sets (section 8); a repeated `ClientId` is success. Screenshots need a connection.
- Rate limits: 30 reports and ratings per user per 10 minutes; 20 browser error reports per user per 10 minutes, each at most 8 KB.

### Errors

- Unhandled server exceptions are recorded by an exception handler that never throws; validation and not-found results are not errors. Browser `error` and `unhandledrejection` events are sent once per page load to an authenticated endpoint, and the last five are kept in `sessionStorage` for the report form. The error page shows a short trace id and a Report this button.

### Notifications and what's new

- When `TELEGRAM_BOT_TOKEN` and `TELEGRAM_CHAT_ID` are set, new reports (not thumbs up) and new error fingerprints send a Telegram message from a background queue: the id, the reporter's name, kind, area and the first 80 characters. Never attachments, snapshots or food text.
- Today shows a dismissible What's new card with changelog entries newer than `ChangelogSeenAt`, in the user's language; an entry linked to the viewer's own report says so. The CLI writes the entries.

### Model accuracy

- The AI model pages in the admin area show, for each model used in the last 90 days, the number of parses, the thumbs-down rate and the correction rate, pooled across both accounts because the model setting is shared. Under 20 parses shows "not enough data".

### CLI

`dotnet GymTracker.Web.dll feedback <command>` (run on wizard as `docker exec gymtracker-app dotnet GymTracker.Web.dll feedback ...`). Logs go to stderr so stdout can be piped. Text output, or `--json`.
- `list [--status new|open|all|<status>] [--since 7d] [--area <area>]`, `show <id>`, `attachment <id>` (raw bytes), `status <id> <status> [--reply ...] [--version <sha>]`.
- `parses [--since 30d] [--corrected] [--export-eval]`, `eval [--parser <model>] [--matcher off|jev] [--limit N]` (re-runs the live parser and matcher on exported cases; never part of the tests).
- `errors [--since 7d]`, `digest [--since <date>]`, `changelog add --en ... --bg ... [--feedback <id>] [--version <sha>]`.
- The app version is the Git commit, passed at build time as `APP_VERSION` and stored as the assembly's informational version.
- Retention: ParseRuns older than 180 days with no feedback, and ErrorEvents last seen more than 90 days ago, are removed by the hourly cleanup job. The data export includes the user's own reports.

---

## 12. Admin

The developer's account (Antoan) has an admin area at `/admin` that does what the feedback CLI does, plus the AI settings. Everything else stays per user.

### Access

Every `/admin` request needs all of these; anything short of the role gets a 404, so the area is not discoverable.
- Signed in, with the `Dev` role. `role grant|revoke dev --email ...` and `role list` in the CLI. The role is checked in the database on every admin request, so a revoke takes effect at once; a grant shows up in a live session within 5 minutes, when the sign-in cookie is refreshed, or at the next sign-in.
- A valid Cloudflare Access token. Cloudflare Access protects `gym.peychev.uk/admin*` (one-time email code, only the developer's address). The app still checks `Cf-Access-Jwt-Assertion` itself: RS256 against the team's certs (cached an hour, refetched for an unknown key id), `aud` equal to `Admin:CloudflareAccess:Audience`, `iss` equal to the team domain, expiry with one minute of skew, and the token's email equal to the account's. In Production the area is off until `Admin:CloudflareAccess:TeamDomain` and `Audience` are set; Development and Testing skip the check unless they are set.
- A passkey unlock: `/admin/unlock` asks for one of the account's own passkeys with user verification required, then sets `__Host-gym.admin` (HttpOnly, Secure, SameSite=Lax like the sign-in cookie, so arriving back from Cloudflare's login keeps the unlock; every admin write is a POST with an anti-forgery token) holding the user id, security stamp and unlock time. It lasts 30 minutes from the last admin request and 4 hours at most, and ends on Lock now, log out, Log out everywhere or a password change. A signed-in `Dev` account without it is sent to the unlock page. The unlock page is reached from Settings, so it follows the account's language (the admin pages themselves are English); its button reads "Unlock with a passkey", and an account with no passkey is told to add one in Settings, with a link there, instead of a button that can only fail.

Admin responses are `Cache-Control: no-store`. When the Access session has expired, an admin page change falls back to a full page load (soft navigation already does this when a fetch fails), which shows Cloudflare's login; the eval progress poll reloads the page when a request fails.

### Pages

- Dashboard: the digest, server version, key status and models, and the unlock's expiry with Lock now.
- Reports: list with the CLI's filters; a report shows everything `feedback show` does with the screenshot inline, and sets status, reply and fixed-in version (defaulting to the running version). Opening a new report marks it Seen.
- Parses and errors as in the CLI; regression cases download as JSONL.
- What's new: list, add and remove.
- AI: the OpenRouter key (set, test, remove; only its last 4 characters are ever shown) and the parser, matcher and onboarding model pickers, with model accuracy for the first two.
- Eval: the CLI's `eval` as a background job, one at a time, limit at most 20, with progress and the result kept in memory until the next job.
- Users: name, email, created date, role, passkey count, last report. Read-only. The admin area shows what users sent as feedback and the parse and error records; it never browses another account's food log, weights or workouts.
- Audit: the latest entries.

### Audit and alerts

- `AdminAudit` rows record unlocks (and failed ones), key and model changes, status and reply changes, changelog changes, eval runs and regression-case downloads, from the web or the CLI, with the time, user, client IP and a short detail. Never a secret. Kept a year; `admin audit [--since 30d]` in the CLI.
- With Telegram configured, each unlock and each key change sends a message.

### CLI additions

- `role grant|revoke dev --email <email>`, `role list`.
- `ai show`, `ai set-model parser|matcher|onboarding|weekly <id>`, `ai set-key` (the key is read from standard input, never an argument), `ai clear-key`, `ai test`.
- `feedback changelog list|remove <id>`, `admin audit [--since 30d]`.

---

## 13. Build phases and acceptance criteria

### Phase 1: Foundation and workout logging
- Solution structure, Docker setup, PostgreSQL, migrations, Identity with the create-user CLI.
- Exercise seed, templates CRUD, start and log sessions, history.
- Bodyweight entry on the Today page.
- Login hardening from section 9, the Cloudflare hostname, and the backup-script changes from section 10.
- **Done when:** both accounts can log a full workout from an iPhone at `gym.peychev.uk` over mobile data; the data lives in `~/docker/gymtracker/db` and persists across `docker compose down` and `up`; the host script's dump restores into a fresh database; lockout and login rate limiting are tested; and user isolation tests pass.

### Phase 2: Progression and training analytics
- Progression service with suggestions in the active session view.
- e1RM, weekly volume, and their Progress charts. Session summary with PRs.
- Rest timer. Offline pending-set queue.
- **Done when:** all Core tests for 5.1 to 5.3 pass; logging sets with airplane mode toggled on and off, and with the app closed or the phone locked mid-request, results in no lost or duplicated sets; and the rest timer sound works on both users' iPhones.

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
- **Done when:** the other account sees read-only AI models and gets a 404 at `/admin`; the developer reaches `/admin` from the iPhone only through the Access code and Face ID; a status and reply set from the admin area show on the reporter's phone; an eval runs from the admin area; and the unlock shows in `admin audit` and on Telegram.

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
- Every finding in `docs/qa/2026-10-05-ui-qa.md`: the workout screen (in-place set edits that save, stored set slots, rows pre-filled from the last set, server-side set numbers, the rest timer's Next and its place beside the Undo toast), the template editor and Settings saving on change, the app's own confirmation sheet, custom exercises and the multi-pick picker, reorder mode, scroll position kept after every change, taps during page transitions, food search ranking and live search, the fallback parser's matches and the "Change food" sheet, entry names without amounts, past-day targets and blocked future days, the totals card labels, chart series with one point, transition aborts kept out of the error log, the visual fixes (segment thumb, value and placeholder colours, scroll margins, light meters, theme switch, title on Back), the Bulgarian text, mixed-script food names, and the suggestions (Last time, Done today, plate bars).
- A browser test project (Playwright, Chromium and WebKit, 390 and 320 px) covers the behaviour that lives in the page.
- **Done when:** each finding has a test that failed before its fix and passes after; `dotnet test` (Core, Web and the browser tests) is green; a repeat of the QA pass in Chrome at 390 and 320 px, in English and Bulgarian, finds none of the listed problems; and the iOS checklist (wake lock, timer sound, Dynamic Type, safe areas, keyboard) passes on both iPhones.

### Later
Ideas kept for after Phase 9, not planned yet. None should start before real logs show fiber coverage is good.
- Protein timing around training: the app knows when sessions happen, so it could suggest protein in the meal before or after.
- Fruit and vegetables in grams, against WHO's 400 g a day.
- Sodium and free sugars (Open Food Facts has both; USDA has most), against WHO's under 2 g sodium and under 10% of energy from free sugars.
- Alcohol as its own energy line, since it is neither a macro the targets account for nor visible in the totals.
- A weekly diet quality score built from protein, fiber, saturated fat and fruit and vegetables.
