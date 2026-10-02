# GymTracker: Product and Technical Specification

Self-hosted training and nutrition tracker for two users (me and my father). Runs in Docker on a headless Ubuntu home server, used from iPhones as an installed PWA, reached remotely over Tailscale. Replaces ad hoc calorie tracking with a system that logs workouts, suggests progression, parses food entries with an LLM, and estimates real maintenance calories from logged data.

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
- Exposing the app to the open internet.

---

## 2. Stack

| Concern | Choice |
|---|---|
| Runtime | .NET 10 (LTS), ASP.NET Core |
| UI | MVC with Razor views, htmx for partial updates, Alpine.js only where client state is needed (rest timer, active set form) |
| Charts | Chart.js |
| CSS | Tailwind via the standalone CLI (no Node build pipeline) or plain CSS; mobile-first, large touch targets |
| Database | PostgreSQL 17, EF Core with Npgsql provider |
| Auth | ASP.NET Core Identity, cookie auth, long-lived persistent login (30 days) |
| LLM | Google Gemini API via typed HttpClient, behind an interface |
| Food database | Open Food Facts API v2 |
| Barcode scanning | `@zxing/browser` loaded from a CDN (Safari on iOS lacks the native `BarcodeDetector` API) |
| Tests | xUnit, FluentAssertions; Testcontainers for PostgreSQL integration tests |
| Deployment | docker-compose; HTTPS via `tailscale serve` |

Server-rendered MVC with htmx is chosen over Blazor Server deliberately: gym signal drops would kill Blazor Server circuits mid-workout, while plain HTTP requests can simply be retried.

---

## 3. Solution structure

```
GymTracker.sln
src/
  GymTracker.Core/            Domain entities, enums, pure calculation services. No EF, no HTTP, no ASP.NET references.
  GymTracker.Infrastructure/  EF DbContext, migrations, repositories, Gemini client, Open Food Facts client.
  GymTracker.Web/             Controllers, views, view models, htmx partials, static assets, PWA manifest and service worker.
tests/
  GymTracker.Core.Tests/            Unit tests for all calculation logic.
  GymTracker.Infrastructure.Tests/  Integration tests against real PostgreSQL via Testcontainers.
docker/
  Dockerfile
  docker-compose.yml
  backup.sh
```

All business math (progression, e1RM, volume, trend weight, maintenance estimate, targets) lives in `GymTracker.Core` as pure, deterministic functions that take data in and return results. Controllers never contain calculations.

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
- `Sex` (Male, Female), `BirthYear`: used only for the initial maintenance guess
- `InitialMaintenanceKcal` (nullable; if null, compute with Mifflin-St Jeor times an activity factor of 1.55)
- `Goal` (Cut, Maintain, Bulk)
- `TargetRatePercentPerWeek` (decimal, e.g. 0.5 means 0.5% of bodyweight per week; ignored for Maintain)
- `ProteinGramsPerKg` (default 2.0)
- `DefaultWeightIncrementKg` (default 2.5)

### Training

**Exercise**
- `Id`, `Name`, `PrimaryMuscle` (enum), `SecondaryMuscles` (list of enum, stored as an array column), `Equipment` (Barbell, Dumbbell, Machine, Cable, Bodyweight, Other)
- `OwnerUserId` (nullable; null means shared seed library, non-null means a user's custom exercise)
- `IsArchived`

Muscle enum: Chest, Back, Shoulders, Biceps, Triceps, Forearms, Quads, Hamstrings, Glutes, Calves, Abs.

Seed about 40 common exercises (bench press, incline dumbbell press, squat, Romanian deadlift, deadlift, pull-up, lat pulldown, barbell row, cable row, overhead press, lateral raise, leg press, leg curl, leg extension, calf raise, curls, triceps pushdown, etc.).

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
- `CompletedAt`
- `ClientId` (Guid generated in the browser, unique index) so retried requests are idempotent.

### Body

**BodyweightEntry**
- `Id`, `UserId`, `Date` (unique per user and date; a second entry the same day overwrites), `WeightKg`

### Nutrition

**FoodItem** (reusable food definitions and cache)
- `Id`, `OwnerUserId` (nullable for shared items such as Open Food Facts cache)
- `Name`, `Brand` (nullable), `Barcode` (nullable, unique where not null)
- `KcalPer100g`, `ProteinPer100g`, `CarbsPer100g`, `FatPer100g`
- `DefaultServingGrams` (nullable)
- `Source` (OpenFoodFacts, Manual, Ai)

**FoodLogEntry**
- `Id`, `UserId`, `Date`, `Meal` (Breakfast, Lunch, Dinner, Snack)
- `Description` (what is shown in the log, e.g. "Chicken breast, grilled")
- `Grams`
- `Kcal`, `ProteinG`, `CarbsG`, `FatG` (stored totals, so the entry is stable even if the FoodItem changes)
- `FoodItemId` (nullable), `Source` (Barcode, Search, Ai, Manual, Recent)
- `CreatedAt`

**MaintenanceEstimate**
- `Id`, `UserId`, `CalculatedOn` (DateOnly)
- `EstimatedTdeeKcal`, `TargetKcal`, `TargetProteinG`
- `TrendWeightKg`, `DaysOfData`, `LoggingCompleteness` (0 to 1), `Method` (Initial, Adaptive)

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

Recalculated on demand when the Today page loads, cached once per user per day in `MaintenanceEstimate`.

1. **Prior:** `InitialMaintenanceKcal` if set, else Mifflin-St Jeor BMR times 1.55 using the latest trend weight.
2. **Window:** the most recent 21 days ending yesterday (today's log is incomplete).
3. **Data requirements:** at least 14 days in the window, food logged on at least 80% of days, and weigh-ins on at least 50% of days. If not met, use the previous estimate (or the prior) and mark `Method = Initial`.
4. **Calculation:**
   - `avgIntake` = mean daily kcal over days that have food logged. Unlogged days are assumed to equal this average.
   - `deltaTrend` = trend weight at window end minus trend weight at window start.
   - `rawTdee = avgIntake - (deltaTrend * 7700 / windowDays)`.
5. **Smoothing:** `newTdee = 0.7 * previousEstimate + 0.3 * rawTdee`, and clamp the change so it moves at most 100 kcal from the previous estimate in one week. Store `Method = Adaptive`.

Test with synthetic data: stable weight at known intake returns that intake; steady loss of 0.5 kg per week at 2,000 kcal converges near 2,550; missing logging days below the threshold falls back to the prior; noisy daily weights do not cause jumps beyond the clamp.

### 5.6 Daily targets

- **Maintain:** `TargetKcal = TDEE`.
- **Cut / Bulk:** daily adjustment = `TrendWeight * TargetRatePercentPerWeek / 100 * 7700 / 7`, subtracted for Cut, added for Bulk.
- **Safety limits:** `TargetRatePercentPerWeek` is capped at 1.0 for Cut and 0.5 for Bulk in profile validation. `TargetKcal` is never below 1,800 for men or 1,500 for women; if the limit applies, show a short note on the Today page.
- `TargetProteinG = TrendWeight * ProteinGramsPerKg`, rounded to 5 g.
- Carbs and fat are displayed but not targeted.

---

## 6. External integrations (GymTracker.Infrastructure)

### 6.1 Food parsing via Gemini

Interface in Core:

```csharp
public interface IFoodParser
{
    Task<FoodParseResult> ParseAsync(string text, CancellationToken ct);
}
```

`FoodParseResult` contains a list of `ParsedFoodItem` (Name, Grams, Kcal, ProteinG, CarbsG, FatG, Confidence Low/Medium/High, Assumption string such as "assumed cooked weight") plus an overall error message if parsing failed.

Implementation `GeminiFoodParser`:
- Typed `HttpClient` calling the Gemini `generateContent` REST endpoint.
- Model name from configuration `Gemini:Model`; do not hardcode. Use the current cheapest Flash-class model.
- API key from configuration `Gemini:ApiKey` (environment variable `Gemini__ApiKey` in Docker). Never commit it.
- Request structured output: `responseMimeType: "application/json"` with a `responseSchema` matching `ParsedFoodItem[]`.
- System prompt: the user writes foods in English or Bulgarian; return one item per food; estimate grams for vague quantities ("a banana", "a plate of rice") and state the assumption; assume raw weight for meat and cooked weight for rice and pasta unless stated; use standard nutrition database values.
- Validation after parsing: if `|Kcal - (4*Protein + 4*Carbs + 9*Fat)|` exceeds 15% of `Kcal`, mark the item Low confidence. Reject negative values or grams above 3,000.
- Timeout 15 seconds, one retry on 5xx or timeout. On failure, the UI falls back to manual entry with the text preserved.
- Results are never saved directly. The user always sees an editable confirmation list first.

Also provide `FakeFoodParser` for tests and local development without a key (selected when `Gemini:ApiKey` is empty).

### 6.2 Open Food Facts

`IFoodDatabase` with `GetByBarcodeAsync(string barcode)` and `SearchAsync(string query)`.
- Barcode: `GET https://world.openfoodfacts.org/api/v2/product/{barcode}.json?fields=product_name,brands,nutriments,serving_quantity`.
- Read `energy-kcal_100g`, `proteins_100g`, `carbohydrates_100g`, `fat_100g`. If kcal is missing but kJ is present, convert by dividing by 4.184.
- Set a descriptive `User-Agent` header as Open Food Facts requires.
- Cache every successful lookup as a shared `FoodItem` so repeat scans are instant and work offline from the API.
- If the product is missing or has no nutrition data, offer to create a manual `FoodItem` with that barcode.

---

## 7. Screens

Mobile-first, designed for one-handed use on an iPhone. Bottom tab bar: **Today, Train, Food, Progress, Settings.** Dark theme by default.

### Today
- Calories remaining and protein remaining as two large progress bars, with consumed and target numbers.
- Weight entry field (pre-focused if no weigh-in today), with the current trend weight shown next to it.
- Next suggested workout: the template after the last one completed, in template sort order, with a Start button. If a session is active, a Resume button instead.
- Small text showing the maintenance estimate and whether it is Initial or Adaptive, with days of data.

### Train
- List of templates with Start buttons; option to start an empty freestyle session.
- **Active session view:**
  - One card per exercise in order. Each card shows the progression suggestion and its reason, then a row per working set with weight and reps inputs pre-filled from the suggestion, and a check button.
  - Tapping the check button saves the set immediately via htmx and starts the rest timer.
  - Add warmup set, add extra set, add exercise, reorder, and remove exercise.
  - Rest timer pinned to the bottom of the screen: counts down from the exercise's rest time, vibrates (`navigator.vibrate` where supported) and plays a short sound at zero, with +30 s and skip buttons.
  - Finish button ends the session and shows a summary: duration, total working sets, new e1RM records.
- **History:** sessions list, newest first; tap to view or edit a past session.
- **Templates:** create, edit, reorder exercises, set rep ranges, sets, increment and rest per exercise.

### Food
- Date selector (defaults to today), entries grouped by meal, daily totals at the top.
- Add entry with four methods:
  1. **Text:** a textarea; submit sends to the parser and shows the editable confirmation list; confirm saves all items.
  2. **Barcode:** opens the camera scanner; on detection, looks up the product and asks for grams (pre-filled with the serving size).
  3. **Search:** searches the user's own FoodItems and recent entries first, then Open Food Facts.
  4. **Recent / favourites:** one-tap re-log of the 20 most frequent entries from the last 30 days, with editable grams.
- Edit or delete any entry. Copy yesterday's meal into today.

### Progress
- Bodyweight chart: raw weigh-ins as dots, trend weight as a line, selectable range (4 weeks, 12 weeks, all).
- Per-exercise chart of best e1RM per session, with an exercise picker.
- Weekly volume per muscle group as a bar chart for the current and previous week.
- Calories: daily intake versus target for the last 4 weeks, with the maintenance estimate as a line.

### Settings
- Profile fields from section 4, with validation including the safety limits in 5.6.
- Custom exercises.
- Export all of the user's data as CSV files in a zip.
- Change password, log out.

---

## 8. PWA and resilience

- Web app manifest with name, icons (192 and 512), `display: standalone`, dark theme colour, start URL `/today`.
- Service worker that caches static assets (CSS, JS, icons) only. Pages and data are always fetched from the server.
- **Set logging must survive bad gym signal:**
  - Each set form submission carries a `ClientId` Guid.
  - If the request fails, the set is stored in `localStorage` in a pending queue and shown with a "pending" marker.
  - The queue retries on the `online` event, on every subsequent successful request, and every 15 seconds while non-empty.
  - The server treats a repeated `ClientId` as success without creating a duplicate.
- The active session must be fully restorable after the app is closed or the phone locks: all state lives on the server, except the pending queue and the rest timer end time (both in `localStorage`).

---

## 9. Security

- Public registration is disabled. Accounts are created with a CLI command: `dotnet GymTracker.Web.dll create-user --email ... --name ...`, which prompts for the password.
- Every controller action requires authentication except login.
- Global EF query filters plus explicit ownership checks on every write; integration tests must verify that user A cannot read or modify user B's sessions, sets, food entries or weigh-ins by ID.
- Anti-forgery tokens on all POSTs, including htmx requests (send the token via `hx-headers`).
- Secrets only from environment variables. Provide `.env.example`; `.env` is in `.gitignore`.
- The app is only reachable through Tailscale; the container does not publish ports beyond the host's loopback.

---

## 10. Deployment

**docker-compose.yml** with:
- `db`: `postgres:17`, named volume for data, healthcheck with `pg_isready`.
- `app`: built from `docker/Dockerfile` (multi-stage build on the .NET 10 SDK image, runtime on the ASP.NET 10 image), depends on a healthy `db`, port bound to `127.0.0.1:8080`, environment from `.env`, `restart: unless-stopped`.
- `backup`: lightweight container running `pg_dump` nightly at 03:00 into a mounted `./backups` directory, keeping the last 14 dumps.

The app applies EF migrations automatically on startup and seeds the exercise library if empty.

HTTPS (required for the camera and PWA install): on the host, run `tailscale serve --bg 8080` so the app is available at `https://<machine-name>.<tailnet>.ts.net` with a valid certificate. Document this in the README.

**README** must cover: prerequisites, `.env` setup, `docker compose up -d`, creating users, the `tailscale serve` command, restoring from a backup, and installing the PWA on iPhone (Safari, Share, Add to Home Screen).

---

## 11. Build phases and acceptance criteria

### Phase 1: Foundation and workout logging
- Solution structure, Docker setup, PostgreSQL, migrations, Identity with the create-user CLI.
- Exercise seed, templates CRUD, start and log sessions, history.
- Bodyweight entry on the Today page.
- **Done when:** both accounts can log a full workout from an iPhone over Tailscale, data persists across container restarts, and user isolation tests pass.

### Phase 2: Progression and training analytics
- Progression service with suggestions in the active session view.
- e1RM, weekly volume, and their Progress charts. Session summary with PRs.
- Rest timer. Offline pending-set queue.
- **Done when:** all Core tests for 5.1 to 5.3 pass, and logging sets with airplane mode toggled on and off results in no lost or duplicated sets.

### Phase 3: Nutrition
- FoodItem and FoodLogEntry, text parsing via Gemini with confirmation, barcode scanning, Open Food Facts lookup and caching, search, recents, copy meal.
- Daily totals on the Food and Today pages, using the prior maintenance estimate.
- **Done when:** a typical day can be logged in under two minutes total, and the app works with `FakeFoodParser` when no API key is set.

### Phase 4: Adaptive targets
- Trend weight, adaptive maintenance, daily targets with safety limits, Progress charts for weight and calories.
- **Done when:** all Core tests for 5.4 to 5.6 pass, including the synthetic convergence scenarios.

### Phase 5: Polish
- CSV export, PWA icons and manifest polish, README completion, backup restore tested once for real.
