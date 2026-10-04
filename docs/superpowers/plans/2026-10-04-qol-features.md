# Quality-of-life Features Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship seven small features: duplicate template, exercise notes and bar weight, plate calculator, swap exercise, quick add, saved meals, and undo instead of confirm dialogs.

**Architecture:** Each feature follows the existing layering: pure calculations in `GymTracker.Core` with xUnit tests, a scoped service in `GymTracker.Web/Features` with Testcontainers tests through `UserScope`, a thin controller action, Razor views, and htmx-loaded bottom sheets for small forms. Undo is soft delete through EF Core 10 named query filters, so every existing query hides deleted rows without changes at call sites.

**Tech Stack:** .NET 10, ASP.NET Core MVC + Razor, htmx 2.0.11, EF Core 10.0.12 + Npgsql 10, xUnit 2.9 + FluentAssertions 7 + Testcontainers (Postgres 18).

**Spec:** `docs/superpowers/specs/2026-10-04-qol-features-design.md`

## Global Constraints

- `GymTracker.Core` has no references to EF Core, ASP.NET Core or HttpClient; calculations live there as pure functions with unit tests written first.
- Every user-owned query is scoped to the current user; never trust ids from the client without an ownership check (the global query filters do this; never call `IgnoreQueryFilters()` without re-checking ownership, and prefer `IgnoreQueryFilters([AppDbContext.SoftDeleteFilter])`).
- No business logic in controllers or views.
- Async EF calls with `CancellationToken` throughout.
- Weights in kg, energy in kcal.
- No new NuGet packages or front-end libraries.
- Mobile-first: check layouts at 390 px; touch targets at least 44 px.
- CSP: no inline scripts or inline event handlers (`ViewHygieneTests`); htmx `allowEval` is false, so no `hx-vals="js:..."` or `hx-on`.
- Every user-facing string goes through `L["..."]`, `LocalText`, `ValidationException("...")` or `TempData.Notice("...")`, and gets a Bulgarian entry in `src/GymTracker.Web/Resources/strings.bg.json`. Strings shown by scripts with `t("...")` must also be listed in `ScriptText.Keys`. `CatalogueTests` enforces both.
- Migrations: `dotnet ef migrations add <Name> --project src/GymTracker.Web --output-dir Data/Migrations` (run `dotnet tool restore` once first).
- Before each commit: `dotnet build` and `dotnet test`, both clean. Small focused commits ending with the attribution lines from the session.
- SPEC.md sections 4 (data model) and 7 (screens) get a line for each feature in the same commit.

## Review Focus

1. Undoing a set after another set of the same kind was logged in its place: both sets show, numbered 1..n without duplicates (Task 7 test `Restoring_a_set_renumbers_its_siblings`).
2. Pressing Undo twice, or Undo on a row that was never deleted: the second call succeeds silently and changes nothing (Task 7 test `Undo_is_idempotent`).
3. Logging a saved meal whose original food is no longer visible (another user's private item or a deleted one): the entry is logged without the food link instead of failing (Task 6 test `Logging_drops_a_food_link_that_is_no_longer_visible`).
4. Plate calculator opened with no weight anywhere (fresh exercise, no suggestion, empty field): the sheet asks for a weight instead of erroring (Task 3 test `Plates_without_a_weight_asks_for_one`).
5. Duplicating a template whose name is already 100 characters: the copy's name is shortened to fit and still ends with the copy suffix (Task 1 test `A_long_name_is_shortened_to_fit`).

---

### Task 1: Duplicate template

**Files:**
- Modify: `src/GymTracker.Web/Features/Training/TemplateService.cs` (add `DuplicateAsync`)
- Modify: `src/GymTracker.Web/Controllers/TemplatesController.cs` (inject `Localizer`, add `Duplicate` action)
- Modify: `src/GymTracker.Web/Views/Templates/Index.cshtml` (menu item)
- Modify: `src/GymTracker.Web/Resources/strings.bg.json`
- Modify: `SPEC.md` (section 7, Train → Templates)
- Test: `tests/GymTracker.Web.Tests/DuplicateTemplateTests.cs`

**Interfaces:**
- Produces: `Task<WorkoutTemplate> TemplateService.DuplicateAsync(int id, Func<string, string> copyName, CancellationToken ct)`; `POST /train/templates/{id}/duplicate` redirects to `/train/templates/{copyId}`.

- [ ] **Step 1: Write the failing tests**

Create `tests/GymTracker.Web.Tests/DuplicateTemplateTests.cs`:

```csharp
using FluentAssertions;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Training;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class DuplicateTemplateTests(PostgresFixture fixture)
{
    private static string Copy(string name) => $"{name} (copy)";

    [Fact]
    public async Task Copies_exercises_and_settings_to_the_end_of_the_list()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var bench = await s.Db.Exercises.FirstAsync(e => e.Name == "Bench press");
        var row = await s.Db.Exercises.FirstAsync(e => e.Name == "Barbell row");
        var original = await s.Templates.CreateAsync("Upper A", default);
        await s.Templates.CreateAsync("Lower A", default);
        await s.Templates.AddExerciseAsync(original.Id, bench.Id, default);
        await s.Templates.AddExerciseAsync(original.Id, row.Id, default);
        var first = (await s.Templates.GetAsync(original.Id, default)).Exercises[0];
        await s.Templates.UpdateExerciseAsync(first.Id, new TemplateExerciseInput(4, 6, 10, 1.25m, 180), default);

        var copy = await s.Templates.DuplicateAsync(original.Id, Copy, default);

        s.Db.ChangeTracker.Clear();
        var loaded = await s.Templates.GetAsync(copy.Id, default);
        loaded.Name.Should().Be("Upper A (copy)");
        loaded.Exercises.Select(e => e.ExerciseId).Should().Equal(bench.Id, row.Id);
        var e0 = loaded.Exercises[0];
        (e0.WorkingSets, e0.RepMin, e0.RepMax, e0.WeightIncrementKg, e0.RestSeconds).Should().Be((4, 6, 10, 1.25m, 180));
        (await s.Templates.ListAsync(default)).Select(t => t.Name).Should().Equal("Upper A", "Lower A", "Upper A (copy)");
        (await s.Templates.GetAsync(original.Id, default)).Exercises.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_long_name_is_shortened_to_fit()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var original = await s.Templates.CreateAsync(new string('a', 100), default);

        var copy = await s.Templates.DuplicateAsync(original.Id, Copy, default);

        copy.Name.Should().HaveLength(100).And.EndWith(" (copy)");
    }

    [Fact]
    public async Task Another_users_template_is_not_found()
    {
        await using var owner = new UserScope(fixture, await fixture.CreateUserAsync());
        var template = await owner.Templates.CreateAsync("Mine", default);
        await using var other = new UserScope(fixture, await fixture.CreateUserAsync());

        await other.Invoking(x => x.Templates.DuplicateAsync(template.Id, Copy, default)).Should().ThrowAsync<NotFoundException>();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/GymTracker.Web.Tests --filter DuplicateTemplateTests`
Expected: build error, `TemplateService` has no `DuplicateAsync`.

- [ ] **Step 3: Implement `DuplicateAsync`**

In `TemplateService.cs`, after `CreateAsync`:

```csharp
    /// <summary>
    /// Copies a template and its exercises to the end of the list. copyName turns "Upper A" into "Upper A (copy)"
    /// in the user's language; the original part is shortened if the result would pass the 100-character limit.
    /// </summary>
    public async Task<WorkoutTemplate> DuplicateAsync(int id, Func<string, string> copyName, CancellationToken ct)
    {
        var source = await GetAsync(id, ct);
        var name = copyName(source.Name);
        if (name.Length > 100)
        {
            name = copyName(source.Name[..Math.Max(1, source.Name.Length - (name.Length - 100))].TrimEnd());
        }

        var maxOrder = await db.Templates.MaxAsync(t => (int?)t.SortOrder, ct) ?? -1;
        var copy = new WorkoutTemplate
        {
            UserId = source.UserId,
            Name = name,
            SortOrder = maxOrder + 1,
            Exercises = source.Exercises.Select(e => new TemplateExercise
            {
                ExerciseId = e.ExerciseId,
                SortOrder = e.SortOrder,
                WorkingSets = e.WorkingSets,
                RepMin = e.RepMin,
                RepMax = e.RepMax,
                WeightIncrementKg = e.WeightIncrementKg,
                RestSeconds = e.RestSeconds,
            }).ToList(),
        };
        db.Templates.Add(copy);
        await db.SaveChangesAsync(ct);
        return copy;
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/GymTracker.Web.Tests --filter DuplicateTemplateTests`
Expected: 3 passed.

- [ ] **Step 5: Controller action and menu item**

In `TemplatesController.cs` add `using GymTracker.Core.Text;` and `using GymTracker.Web.Features.Localization;`, add `Localizer localizer` to the primary constructor, and add after `Move`:

```csharp
    [HttpPost("{id:int}/duplicate")]
    public async Task<IActionResult> Duplicate(int id, CancellationToken ct)
    {
        var copy = await templates.DuplicateAsync(id, name => localizer[new LocalText("{0} (copy)", name)], ct);
        return Redirect($"/train/templates/{copy.Id}");
    }
```

In `Views/Templates/Index.cshtml`, inside `<div class="menu" role="menu">`, after the Edit link:

```cshtml
                        <form method="post" asp-antiforgery="true" action="/train/templates/@template.Id/duplicate"><button type="submit" role="menuitem">@L["Duplicate"]@Ui.Icon("copy")</button></form>
```

- [ ] **Step 6: Translations and SPEC**

Run `GYM_WRITE_KEYS=1 dotnet test tests/GymTracker.Web.Tests --filter CatalogueTests`, then add to `strings.bg.json` (keep the file's alphabetical key order):

```json
  "{0} (copy)": "{0} (копие)",
  "Duplicate": "Дублирай",
```

In SPEC.md section 7, Train, extend the Templates bullet: "duplicate a template (copied to the end of the list as "Name (copy)")".

- [ ] **Step 7: Check in the browser, build, test, commit**

Start the app (`docker compose -f docker/docker-compose.dev.yml up -d db`, `dotnet run --project src/GymTracker.Web`), sign in as `demo@example.test` / `demo-password-123`, open Train → Templates at 390 px, use "Duplicate" on the last row (its menu must be fully visible), and confirm the copy's edit page opens.

Run: `dotnet build` (0 warnings) and `dotnet test` (all pass).

```bash
git add src/GymTracker.Web tests/GymTracker.Web.Tests/DuplicateTemplateTests.cs SPEC.md
git commit -m "Duplicate a template from its menu"
```

---

### Task 2: Exercise notes and bar weight

**Files:**
- Modify: `src/GymTracker.Core/Domain/Training.cs` (add `UserExerciseSettings`)
- Modify: `src/GymTracker.Core/Domain/UserProfile.cs` (bar weight and plates)
- Modify: `src/GymTracker.Core/Domain/ProfileRules.cs` (validate them)
- Create: `src/GymTracker.Core/Training/PlateMath.cs` (`BarFor` now; `PerSide` in Task 3)
- Modify: `src/GymTracker.Web/Data/AppDbContext.cs`
- Create: migration `AddExerciseSettings`
- Create: `src/GymTracker.Web/Features/Training/ExerciseSettingsService.cs`
- Create: `src/GymTracker.Web/Controllers/ExercisesController.cs`
- Create: `src/GymTracker.Web/Views/Shared/_ExerciseSettingsForm.cshtml`, `src/GymTracker.Web/Views/Shared/_ExerciseSheet.cshtml`
- Modify: `src/GymTracker.Web/ViewModels/ViewModels.cs`, `src/GymTracker.Web/Controllers/TrainController.cs`, `src/GymTracker.Web/Controllers/TemplatesController.cs`, `src/GymTracker.Web/Controllers/SettingsController.cs`
- Modify: `src/GymTracker.Web/Views/Shared/_ExerciseCard.cshtml`, `src/GymTracker.Web/Views/Train/Session.cshtml`, `src/GymTracker.Web/Views/Templates/Edit.cshtml`, `src/GymTracker.Web/Views/Settings/Index.cshtml`
- Modify: `src/GymTracker.Web/wwwroot/js/motion.js` (close menus when a sheet opens), `src/GymTracker.Web/wwwroot/css/app.css` (`.ex-note`)
- Modify: `src/GymTracker.Web/Program.cs`, `tests/GymTracker.Web.Tests/UserScope.cs`
- Test: `tests/GymTracker.Core.Tests/PlateMathTests.cs`, `tests/GymTracker.Web.Tests/ExerciseSettingsTests.cs`

**Interfaces:**
- Produces: `class UserExerciseSettings { string UserId; int ExerciseId; string? Note; decimal? BarWeightKg; const int MaxNoteLength = 200; }`
- Produces: `UserProfile.BarWeightKg` (decimal, default 20), `UserProfile.PlatesKg` (`List<decimal>`, default `UserProfile.DefaultPlatesKg`), `UserProfile.PlateChoicesKg` (`decimal[]`).
- Produces: `static decimal? PlateMath.BarFor(Equipment equipment, decimal? exerciseBarKg, decimal profileBarKg)`
- Produces: `ExerciseSettingsService.ForAsync(IEnumerable<int> exerciseIds, CancellationToken) : Task<Dictionary<int, UserExerciseSettings>>`, `GetAsync(int exerciseId, CancellationToken) : Task<UserExerciseSettings?>`, `SaveAsync(int exerciseId, string? note, decimal? barWeightKg, CancellationToken) : Task`
- Produces: `AppDbContext.ExerciseSettings` DbSet.
- Produces: `ExerciseCardViewModel(SessionExercise Exercise, ProgressionSuggestion? Suggestion, string? Note = null, decimal? BarKg = null)`, `SessionViewModel(WorkoutSession Session, IReadOnlyDictionary<int, ProgressionSuggestion?> Suggestions, IReadOnlyDictionary<int, UserExerciseSettings> Settings, decimal ProfileBarKg)`, `TemplateEditViewModel(WorkoutTemplate Template, IReadOnlyDictionary<int, UserExerciseSettings> Settings)`.
- Produces: `GET /train/exercises/{exerciseId}/settings?returnUrl=` (partial `_ExerciseSettingsForm`), `POST /train/exercises/{exerciseId}/settings` (redirects to the local `returnUrl`). A shared sheet `#exercise-sheet` with body `#exercise-sheet-body` (partial `_ExerciseSheet`) that Tasks 3 and later reuse the pattern of.

- [ ] **Step 1: Write the failing Core tests**

Create `tests/GymTracker.Core.Tests/PlateMathTests.cs`:

```csharp
using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Training;

namespace GymTracker.Core.Tests;

public class PlateMathTests
{
    [Fact]
    public void An_exercise_bar_weight_wins_over_the_profile() =>
        PlateMath.BarFor(Equipment.Barbell, 10m, 20m).Should().Be(10m);

    [Fact]
    public void Barbell_exercises_use_the_profile_bar_by_default() =>
        PlateMath.BarFor(Equipment.Barbell, null, 20m).Should().Be(20m);

    [Fact]
    public void Other_equipment_has_no_bar_unless_one_is_set()
    {
        PlateMath.BarFor(Equipment.Machine, null, 20m).Should().BeNull();
        PlateMath.BarFor(Equipment.Machine, 0m, 20m).Should().Be(0m, "a plate-loaded machine opts in with 0");
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/GymTracker.Core.Tests --filter PlateMathTests`
Expected: build error, `PlateMath` does not exist.

- [ ] **Step 3: Implement `PlateMath.BarFor`**

Create `src/GymTracker.Core/Training/PlateMath.cs`:

```csharp
using GymTracker.Core.Domain;

namespace GymTracker.Core.Training;

public static class PlateMath
{
    /// <summary>
    /// The bar to load plates onto: the exercise's own bar weight if set, else the profile's bar for barbell
    /// exercises. Null means the exercise has no plate calculator.
    /// </summary>
    public static decimal? BarFor(Equipment equipment, decimal? exerciseBarKg, decimal profileBarKg) =>
        exerciseBarKg ?? (equipment == Equipment.Barbell ? profileBarKg : null);
}
```

Run: `dotnet test tests/GymTracker.Core.Tests --filter PlateMathTests`
Expected: 3 passed.

- [ ] **Step 4: Domain and profile fields**

Append to `src/GymTracker.Core/Domain/Training.cs`:

```csharp
/// <summary>One user's notes and bar weight for an exercise. Exercises are shared seed rows, so these live apart.</summary>
public class UserExerciseSettings
{
    public const int MaxNoteLength = 200;

    public string UserId { get; set; } = "";
    public int ExerciseId { get; set; }
    public string? Note { get; set; }

    /// <summary>Null uses the profile's bar for barbell exercises; any value turns the plate calculator on.</summary>
    public decimal? BarWeightKg { get; set; }
}
```

In `UserProfile.cs` add:

```csharp
    /// <summary>The plates the calculator offers by default: a common gym set.</summary>
    public static readonly decimal[] DefaultPlatesKg = [25m, 20m, 15m, 10m, 5m, 2.5m, 1.25m];

    /// <summary>Every plate Settings lets you tick.</summary>
    public static readonly decimal[] PlateChoicesKg = [25m, 20m, 15m, 10m, 5m, 2.5m, 2m, 1.25m, 1m, 0.5m];

    public decimal BarWeightKg { get; set; } = 20m;
    public List<decimal> PlatesKg { get; set; } = [.. DefaultPlatesKg];
```

In `ProfileRules.Validate`, before the final `return null;`:

```csharp
        if (p.BarWeightKg is < 0 or > 50)
        {
            return "Bar weight must be between 0 and 50 kg.";
        }

        if (p.PlatesKg.Any(w => !UserProfile.PlateChoicesKg.Contains(w)))
        {
            return "Plate weights must be from the list.";
        }
```

- [ ] **Step 5: DbContext and migration**

In `AppDbContext.cs` add the DbSet:

```csharp
    public DbSet<UserExerciseSettings> ExerciseSettings => Set<UserExerciseSettings>();
```

In the `UserProfile` entity block add:

```csharp
            e.Property(p => p.BarWeightKg).HasPrecision(5, 2);
```

After the `SetEntry` block add:

```csharp
        builder.Entity<UserExerciseSettings>(e =>
        {
            e.HasKey(x => new { x.UserId, x.ExerciseId });
            e.Property(x => x.Note).HasMaxLength(UserExerciseSettings.MaxNoteLength);
            e.Property(x => x.BarWeightKg).HasPrecision(5, 2);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Exercise>().WithMany().HasForeignKey(x => x.ExerciseId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => x.UserId == CurrentUserId);
        });
```

Run: `dotnet tool restore && dotnet ef migrations add AddExerciseSettings --project src/GymTracker.Web --output-dir Data/Migrations`

Open the generated migration and set the defaults existing profiles get. Replace the two `AddColumn` calls for `Profiles` so they read:

```csharp
            migrationBuilder.AddColumn<decimal>(
                name: "BarWeightKg",
                table: "Profiles",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 20m);

            migrationBuilder.AddColumn<List<decimal>>(
                name: "PlatesKg",
                table: "Profiles",
                type: "numeric[]",
                nullable: false,
                defaultValueSql: "'{25,20,15,10,5,2.5,1.25}'::numeric[]");
```

(Keep the generated table name if it differs from `Profiles`; check the `CreateTable` for the profile entity in an earlier migration.)

- [ ] **Step 6: Write the failing service tests**

Add to `tests/GymTracker.Web.Tests/UserScope.cs` a property and its construction:

```csharp
        ExerciseSettings = new ExerciseSettingsService(Db, currentUser);
```

```csharp
    public ExerciseSettingsService ExerciseSettings { get; }
```

Create `tests/GymTracker.Web.Tests/ExerciseSettingsTests.cs`:

```csharp
using FluentAssertions;
using GymTracker.Web.Features;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class ExerciseSettingsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Notes_are_per_user()
    {
        await using var a = new UserScope(fixture, await fixture.CreateUserAsync());
        await using var b = new UserScope(fixture, await fixture.CreateUserAsync());
        var press = await a.Db.Exercises.FirstAsync(e => e.Name == "Leg press");

        await a.ExerciseSettings.SaveAsync(press.Id, "  seat 4  ", null, default);
        await b.ExerciseSettings.SaveAsync(press.Id, "seat 7", 0m, default);

        (await a.ExerciseSettings.GetAsync(press.Id, default))!.Note.Should().Be("seat 4");
        var mine = await b.ExerciseSettings.GetAsync(press.Id, default);
        (mine!.Note, mine.BarWeightKg).Should().Be(("seat 7", 0m));
    }

    [Fact]
    public async Task Saving_nothing_removes_the_row()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var squat = await s.Db.Exercises.FirstAsync(e => e.Name == "Squat");
        await s.ExerciseSettings.SaveAsync(squat.Id, "low bar", 20m, default);

        await s.ExerciseSettings.SaveAsync(squat.Id, "   ", null, default);

        (await s.ExerciseSettings.GetAsync(squat.Id, default)).Should().BeNull();
    }

    [Fact]
    public async Task Long_notes_and_impossible_bars_are_rejected()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var squat = await s.Db.Exercises.FirstAsync(e => e.Name == "Squat");

        await s.Invoking(x => x.ExerciseSettings.SaveAsync(squat.Id, new string('x', 201), null, default)).Should().ThrowAsync<ValidationException>();
        await s.Invoking(x => x.ExerciseSettings.SaveAsync(squat.Id, null, 51m, default)).Should().ThrowAsync<ValidationException>();
        await s.Invoking(x => x.ExerciseSettings.SaveAsync(squat.Id, null, -1m, default)).Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Another_users_custom_exercise_is_not_found()
    {
        await using var owner = new UserScope(fixture, await fixture.CreateUserAsync());
        var custom = new GymTracker.Core.Domain.Exercise { Name = "Secret curl", OwnerUserId = owner.UserId };
        owner.Db.Exercises.Add(custom);
        await owner.Db.SaveChangesAsync();
        await using var other = new UserScope(fixture, await fixture.CreateUserAsync());

        await other.Invoking(x => x.ExerciseSettings.SaveAsync(custom.Id, "mine now", null, default)).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Profile_defaults_to_a_20_kg_bar_and_a_standard_plate_set()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var profile = await s.Db.Profiles.SingleAsync();
        profile.BarWeightKg.Should().Be(20m);
        profile.PlatesKg.Should().Equal(25m, 20m, 15m, 10m, 5m, 2.5m, 1.25m);
    }
}
```

Run: `dotnet test tests/GymTracker.Web.Tests --filter ExerciseSettingsTests`
Expected: build error, `ExerciseSettingsService` does not exist.

- [ ] **Step 7: Implement the service**

Create `src/GymTracker.Web/Features/Training/ExerciseSettingsService.cs`:

```csharp
using GymTracker.Core.Domain;
using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Training;

/// <summary>The signed-in user's notes and bar weights per exercise.</summary>
public class ExerciseSettingsService(AppDbContext db, ICurrentUser currentUser)
{
    public async Task<Dictionary<int, UserExerciseSettings>> ForAsync(IEnumerable<int> exerciseIds, CancellationToken ct)
    {
        var ids = exerciseIds.Distinct().ToList();
        return await db.ExerciseSettings.AsNoTracking().Where(x => ids.Contains(x.ExerciseId)).ToDictionaryAsync(x => x.ExerciseId, ct);
    }

    public Task<UserExerciseSettings?> GetAsync(int exerciseId, CancellationToken ct) =>
        db.ExerciseSettings.AsNoTracking().SingleOrDefaultAsync(x => x.ExerciseId == exerciseId, ct);

    /// <summary>Saves the note and bar weight; when both are empty the row is removed.</summary>
    public async Task SaveAsync(int exerciseId, string? note, decimal? barWeightKg, CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new InvalidOperationException("No signed-in user.");
        if (!await db.Exercises.AnyAsync(e => e.Id == exerciseId, ct))
        {
            throw new NotFoundException("Exercise not found.");
        }

        var trimmed = note?.Trim();
        if (trimmed?.Length > UserExerciseSettings.MaxNoteLength)
        {
            throw new ValidationException("Notes can be at most 200 characters.");
        }

        if (barWeightKg is < 0 or > 50)
        {
            throw new ValidationException("Bar weight must be between 0 and 50 kg.");
        }

        var row = await db.ExerciseSettings.SingleOrDefaultAsync(x => x.ExerciseId == exerciseId, ct);
        if (string.IsNullOrEmpty(trimmed) && barWeightKg is null)
        {
            if (row is not null)
            {
                db.ExerciseSettings.Remove(row);
            }
        }
        else
        {
            if (row is null)
            {
                row = new UserExerciseSettings { UserId = userId, ExerciseId = exerciseId };
                db.ExerciseSettings.Add(row);
            }

            row.Note = string.IsNullOrEmpty(trimmed) ? null : trimmed;
            row.BarWeightKg = barWeightKg;
        }

        await db.SaveChangesAsync(ct);
    }
}
```

Register in `Program.cs` after `builder.Services.AddScoped<ExerciseService>();`:

```csharp
builder.Services.AddScoped<ExerciseSettingsService>();
```

Run: `dotnet test tests/GymTracker.Web.Tests --filter ExerciseSettingsTests`
Expected: 5 passed.

- [ ] **Step 8: View models and controllers**

In `ViewModels.cs` replace `SessionViewModel` and `ExerciseCardViewModel` and add `TemplateEditViewModel` and `ExerciseSettingsFormViewModel`:

```csharp
public record SessionViewModel(
    WorkoutSession Session,
    IReadOnlyDictionary<int, ProgressionSuggestion?> Suggestions,
    IReadOnlyDictionary<int, UserExerciseSettings> Settings,
    decimal ProfileBarKg)
{
    public ExerciseCardViewModel CardFor(SessionExercise exercise)
    {
        var settings = Settings.GetValueOrDefault(exercise.ExerciseId);
        return new ExerciseCardViewModel(
            exercise,
            Suggestions.GetValueOrDefault(exercise.Id),
            settings?.Note,
            PlateMath.BarFor(exercise.Exercise.Equipment, settings?.BarWeightKg, ProfileBarKg));
    }
}

public record ExerciseCardViewModel(SessionExercise Exercise, ProgressionSuggestion? Suggestion, string? Note = null, decimal? BarKg = null)
{
    /// <summary>The target for the n-th working set (0-based), repeating the last one past the planned count.</summary>
    public SetTarget? TargetFor(int index) =>
        Suggestion is { Sets.Count: > 0 } s ? s.Sets[Math.Min(index, s.Sets.Count - 1)] : null;
}

public record TemplateEditViewModel(WorkoutTemplate Template, IReadOnlyDictionary<int, UserExerciseSettings> Settings);

public record ExerciseSettingsFormViewModel(Exercise Exercise, UserExerciseSettings? Settings, decimal ProfileBarKg, string ReturnUrl);
```

(`PlateMath` needs `using GymTracker.Core.Training;`, already imported in that file.)

In `TrainController.cs` inject `ExerciseSettingsService settings` and `ProfileService profiles` (`using GymTracker.Web.Features.Profile;`) and change `Session`:

```csharp
    [HttpGet("sessions/{id:int}")]
    public async Task<IActionResult> Session(int id, CancellationToken ct)
    {
        var session = await sessions.GetAsync(id, ct);
        return View(new SessionViewModel(
            session,
            await analytics.SuggestionsAsync(session, ct),
            await settings.ForAsync(session.Exercises.Select(e => e.ExerciseId), ct),
            (await profiles.GetAsync(ct)).BarWeightKg));
    }
```

In `TemplatesController.cs` inject `ExerciseSettingsService settings` and change `Edit`:

```csharp
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var template = await templates.GetAsync(id, ct);
        return View(new TemplateEditViewModel(template, await settings.ForAsync(template.Exercises.Select(e => e.ExerciseId), ct)));
    }
```

Create `src/GymTracker.Web/Controllers/ExercisesController.cs`:

```csharp
using GymTracker.Web.Features;
using GymTracker.Web.Features.Profile;
using GymTracker.Web.Features.Training;
using GymTracker.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Controllers;

[Route("train/exercises")]
public class ExercisesController(ExerciseSettingsService settings, ProfileService profiles, GymTracker.Web.Data.AppDbContext db) : Controller
{
    /// <summary>The Notes and bar form, loaded into the exercise sheet.</summary>
    [HttpGet("{id:int}/settings")]
    public async Task<IActionResult> Settings(int id, string? returnUrl, CancellationToken ct)
    {
        var exercise = await db.Exercises.SingleOrDefaultAsync(e => e.Id == id, ct) ?? throw new NotFoundException("Exercise not found.");
        return PartialView("_ExerciseSettingsForm", new ExerciseSettingsFormViewModel(
            exercise, await settings.GetAsync(id, ct), (await profiles.GetAsync(ct)).BarWeightKg, Url.SafeReturnUrl(returnUrl)));
    }

    [HttpPost("{id:int}/settings")]
    public async Task<IActionResult> Save(int id, string? note, decimal? barWeightKg, string? returnUrl, CancellationToken ct)
    {
        await settings.SaveAsync(id, note, barWeightKg, ct);
        return Redirect(Url.SafeReturnUrl(returnUrl));
    }
}
```

(Reading `db.Exercises` directly here is a lookup, not business logic; the exercise query filter scopes it to the shared library plus the user's own exercises.)

- [ ] **Step 9: Views**

Create `src/GymTracker.Web/Views/Shared/_ExerciseSheet.cshtml`:

```cshtml
@* One sheet for per-exercise forms (Notes & bar). Menu items load its body with htmx and open it. *@
<div class="sheet" id="exercise-sheet" hidden>
    <div class="sheet-scrim"></div>
    <div class="sheet-panel" role="dialog" aria-modal="true" aria-labelledby="exercise-sheet-title">
        <div class="sheet-grabber" aria-hidden="true"></div>
        <div id="exercise-sheet-body"></div>
    </div>
</div>
```

Create `src/GymTracker.Web/Views/Shared/_ExerciseSettingsForm.cshtml`:

```cshtml
@model ExerciseSettingsFormViewModel
<div class="sheet-head"><span></span><span class="sheet-title" id="exercise-sheet-title">@L.Exercise(Model.Exercise)</span><button type="button" class="sheet-close" data-sheet-close>@L["Cancel"]</button></div>
<form method="post" asp-antiforgery="true" action="/train/exercises/@Model.Exercise.Id/settings">
    <input type="hidden" name="returnUrl" value="@Model.ReturnUrl" />
    <div class="list">
        <div class="field-row stacked">
            <label for="ex-note">@L["Note"]</label>
            <textarea id="ex-note" name="note" rows="2" maxlength="200" class="textarea" style="background: none; padding: 0; box-shadow: none" placeholder="@L["For example: seat 4, neutral grip"]">@Model.Settings?.Note</textarea>
        </div>
        <div class="field-row"><label for="ex-bar">@L["Bar weight"]</label><input id="ex-bar" class="num" type="number" name="barWeightKg" value="@(Model.Settings?.BarWeightKg?.Kg())" min="0" max="50" step="any" inputmode="decimal" placeholder="@Model.ProfileBarKg.Kg()" /><span class="unit">@L["kg"]</span></div>
    </div>
    <p class="section-footer">@L["Leave the bar empty to use {0} kg for barbell exercises. Set it on any exercise to get the plate calculator, 0 for a plate-loaded machine.", Model.ProfileBarKg.Kg()]</p>
    <button type="submit" class="btn btn-primary btn-block" style="margin-top: var(--s-4)">@L["Save"]</button>
</form>
```

In `_ExerciseCard.cshtml`:
- Under `<h2 class="ex-name">@name</h2>` add:

```cshtml
            @if (Model.Note is { } note)
            {
                <p class="ex-note">@note</p>
            }
```

- In the menu, before the Remove form, add:

```cshtml
                <button type="button" role="menuitem" hx-get="/train/exercises/@Ex.ExerciseId/settings?returnUrl=@Uri.EscapeDataString($"/train/sessions/{Ex.SessionId}")" hx-target="#exercise-sheet-body" data-sheet-open="exercise-sheet">@L["Notes & bar"]@Ui.Icon("pencil")</button>
```

In `Views/Train/Session.cshtml`:
- Replace the card partial call with `<partial name="_ExerciseCard" model="Model.CardFor(exercise)" />`.
- In `@section Overlays`, outside the `@if (session.IsActive)` block, add `<partial name="_ExerciseSheet" />`.

In `Views/Templates/Edit.cshtml`:
- Change `@model WorkoutTemplate` to `@model TemplateEditViewModel`, and at the top of the code block add `var template = Model.Template;`; replace every `Model.Name`, `Model.Id`, `Model.Exercises` in the file with `template.Name`, `template.Id`, `template.Exercises`.
- Under `<div><h2 class="ex-name">@L.Exercise(item.Exercise)</h2></div>` change it to:

```cshtml
            <div>
                <h2 class="ex-name">@L.Exercise(item.Exercise)</h2>
                @if (Model.Settings.GetValueOrDefault(item.ExerciseId)?.Note is { } note)
                {
                    <p class="ex-note">@note</p>
                }
            </div>
```

- In that menu, before the Remove form, add:

```cshtml
                    <button type="button" role="menuitem" hx-get="/train/exercises/@item.ExerciseId/settings?returnUrl=@Uri.EscapeDataString($"/train/templates/{template.Id}")" hx-target="#exercise-sheet-body" data-sheet-open="exercise-sheet">@L["Notes & bar"]@Ui.Icon("pencil")</button>
```

- At the end of the file add `@section Overlays { <partial name="_ExerciseSheet" /> }`.

In `app.css`, after the `.ex-name` rule:

```css
.ex-note { margin-top: 2px; font-size: var(--text-footnote); color: var(--label-2); overflow-wrap: anywhere; }
```

In `motion.js`, in the document click handler for `[data-sheet-open]`, close any open context menu first:

```js
        if (opener) {
            event.preventDefault();
            closeMenus();
            showSheet(document.getElementById(opener.dataset.sheetOpen));
            return;
        }
```

(`closeMenus` is defined further down in the same IIFE as a function declaration, so it is hoisted.)

- [ ] **Step 10: Settings: bar and plates**

In `SettingsController.ProfileForm` add `decimal? BarWeightKg, List<decimal>? PlatesKg` at the end of the record, and in `Profile`:

```csharp
            p.BarWeightKg = form.BarWeightKg ?? 20m;
            p.PlatesKg = (form.PlatesKg ?? []).Distinct().OrderDescending().ToList();
```

In `Views/Settings/Index.cshtml`, inside the profile `<form>`, after the Goal section's closing `</section>` and before the Save button's section closes, add a new section (move the "Save profile" button to the end of this new section):

```cshtml
    <section class="section">
        <h2 class="section-title">@L["Bar and plates"]</h2>
        <div class="list">
            <div class="field-row"><label for="bar">@L["Bar weight"]</label><input id="bar" class="num" type="number" name="barWeightKg" value="@Model.BarWeightKg.Kg()" min="0" max="50" step="any" inputmode="decimal" /><span class="unit">@L["kg"]</span></div>
            <fieldset class="field-row stacked" style="border: 0; margin: 0">
                <legend class="sr-only">@L["Plates you have"]</legend>
                <span aria-hidden="true">@L["Plates you have"]</span>
                <div class="plate-choices">
                    @foreach (var plate in UserProfile.PlateChoicesKg)
                    {
                        <label class="plate-choice"><input type="checkbox" name="platesKg" value="@plate.Kg()" checked="@Model.PlatesKg.Contains(plate)" /><span class="num">@plate.Kg()</span></label>
                    }
                </div>
            </fieldset>
        </div>
        <p class="section-footer">@L["The plate calculator uses this bar and these plates. Set a different bar for one exercise under Notes & bar."]</p>
        <button type="submit" class="btn btn-primary btn-block" style="margin-top: var(--s-4)">@L["Save profile"]</button>
    </section>
```

In `app.css` add:

```css
.plate-choices { display: flex; flex-wrap: wrap; gap: var(--s-2); margin-top: var(--s-2); }
.plate-choice { position: relative; display: inline-flex; }
.plate-choice input { position: absolute; opacity: 0; inset: 0; margin: 0; }
.plate-choice span { display: inline-grid; place-items: center; min-width: 52px; min-height: var(--tap); padding: 0 var(--s-3); border-radius: var(--r-md); background: var(--fill); color: var(--label); font-weight: var(--weight-semibold); }
.plate-choice input:checked + span { background: var(--accent); color: var(--on-accent); }
.plate-choice input:focus-visible + span { outline: 2px solid var(--accent); outline-offset: 2px; }
```

Note: the checkbox values use `Kg()` formatting, which prints invariant decimals ("2.5"); the form binder must read them invariantly. Verify by saving with 2.5 ticked in Bulgarian and English and checking the profile row; if Bulgarian culture binds "2.5" wrong, post the value multiplied by 100 as an int (`platesCg`) and divide in the controller instead.

- [ ] **Step 11: Translations, SPEC, browser check, commit**

Run `GYM_WRITE_KEYS=1 dotnet test tests/GymTracker.Web.Tests --filter CatalogueTests`, then add (skip any key already present):

```json
  "Bar and plates": "Лост и дискове",
  "Bar weight": "Тегло на лоста",
  "Bar weight must be between 0 and 50 kg.": "Теглото на лоста трябва да е между 0 и 50 кг.",
  "For example: seat 4, neutral grip": "Например: седалка 4, неутрален хват",
  "Leave the bar empty to use {0} kg for barbell exercises. Set it on any exercise to get the plate calculator, 0 for a plate-loaded machine.": "Оставете лоста празен, за да се ползват {0} кг за упражнения с щанга. Задайте го за всяко упражнение, за да получите калкулатор за дискове; 0 за машина с дискове.",
  "Note": "Бележка",
  "Notes & bar": "Бележки и лост",
  "Notes can be at most 200 characters.": "Бележките могат да са най-много 200 знака.",
  "Plate weights must be from the list.": "Дисковете трябва да са от списъка.",
  "Plates you have": "Дискове, които имате",
  "The plate calculator uses this bar and these plates. Set a different bar for one exercise under Notes & bar.": "Калкулаторът за дискове използва този лост и тези дискове. Друг лост за отделно упражнение се задава от „Бележки и лост“.",
```

SPEC.md section 4 (Training): add `UserExerciseSettings` (user, exercise, note up to 200 characters, optional bar weight); section 4 (Users and profile): bar weight (default 20 kg) and plate list. Section 7, Train and Settings: the Notes & bar sheet and the Bar and plates section.

Browser check at 390 px: open an active session, use "Notes & bar" on an exercise, save a note, see it under the name; open the template edit page and see the same note; Settings shows the plate chips at 44 px height and saving keeps the ticks.

Run `dotnet build` and `dotnet test`.

```bash
git add src tests SPEC.md
git commit -m "Add per-exercise notes and bar weight, and plates in Settings"
```

---

### Task 3: Plate calculator

**Files:**
- Modify: `src/GymTracker.Core/Training/PlateMath.cs` (add `PerSide` and `PlateLoad`)
- Modify: `tests/GymTracker.Core.Tests/PlateMathTests.cs`
- Modify: `src/GymTracker.Web/Controllers/TrainController.cs` (plates endpoint)
- Modify: `src/GymTracker.Web/Features/Training/SessionService.cs` (`LastWorkingWeightAsync`)
- Create: `src/GymTracker.Web/Views/Shared/_Plates.cshtml`
- Modify: `src/GymTracker.Web/ViewModels/ViewModels.cs` (`PlatesViewModel`)
- Modify: `src/GymTracker.Web/Views/Shared/_ExerciseCard.cshtml`, `src/GymTracker.Web/Views/Train/Session.cshtml`, `src/GymTracker.Web/wwwroot/css/app.css`
- Test: `tests/GymTracker.Web.Tests/PlatesEndpointTests.cs`

**Interfaces:**
- Consumes: `PlateMath.BarFor`, `ExerciseCardViewModel.BarKg`, `ExerciseSettingsService.GetAsync`, `UserProfile.PlatesKg`, `UserProfile.BarWeightKg`.
- Produces: `record PlateLoad(IReadOnlyList<decimal> PerSide, decimal TotalKg, bool Exact, bool BelowBar)`; `static PlateLoad PlateMath.PerSide(decimal targetKg, decimal barKg, IEnumerable<decimal> platesKg)`.
- Produces: `GET /train/session-exercises/{id}/plates?weight=&suggested=` returning partial `_Plates`; `record PlatesViewModel(int SessionExerciseId, decimal BarKg, decimal? TargetKg, PlateLoad? Load)`.

- [ ] **Step 1: Write the failing Core tests**

Append to `PlateMathTests`:

```csharp
    private static readonly decimal[] Standard = [25m, 20m, 15m, 10m, 5m, 2.5m, 1.25m];

    [Fact]
    public void Loads_the_fewest_plates_heaviest_first()
    {
        var load = PlateMath.PerSide(100m, 20m, Standard);
        (load.PerSide, load.TotalKg, load.Exact).Should().BeEquivalentTo((new[] { 25m, 15m }, 100m, true), o => o.WithStrictOrdering());
    }

    [Fact]
    public void Small_plates_make_up_the_rest() =>
        PlateMath.PerSide(82.5m, 20m, Standard).PerSide.Should().Equal(25m, 5m, 1.25m);

    [Fact]
    public void Searches_every_combination_instead_of_taking_the_heaviest_plate() =>
        PlateMath.PerSide(60m, 20m, [15m, 10m]).PerSide.Should().Equal(10m, 10m);

    [Fact]
    public void An_unreachable_weight_gives_the_closest_lower_load()
    {
        var load = PlateMath.PerSide(83m, 20m, Standard);
        (load.TotalKg, load.Exact).Should().Be((82.5m, false));
    }

    [Fact]
    public void The_bar_alone_is_exact() =>
        PlateMath.PerSide(20m, 20m, Standard).Should().BeEquivalentTo(new PlateLoad([], 20m, true, false));

    [Fact]
    public void Less_than_the_bar_is_flagged() =>
        PlateMath.PerSide(15m, 20m, Standard).Should().BeEquivalentTo(new PlateLoad([], 20m, false, true));

    [Fact]
    public void No_plates_means_only_the_bar()
    {
        var load = PlateMath.PerSide(60m, 20m, []);
        (load.PerSide.Count, load.TotalKg, load.Exact).Should().Be((0, 20m, false));
    }

    [Fact]
    public void Heavy_loads_work() =>
        PlateMath.PerSide(300m, 20m, Standard).PerSide.Should().Equal(25m, 25m, 25m, 25m, 25m, 15m);

    [Fact]
    public void Ignores_plates_that_are_not_positive_multiples_of_50_grams() =>
        PlateMath.PerSide(22.5m, 20m, [1.25m, 0m, -5m, 1.13m]).PerSide.Should().Equal(1.25m);

    [Fact]
    public void A_zero_kilo_bar_works_for_plate_loaded_machines() =>
        PlateMath.PerSide(90m, 0m, Standard).PerSide.Should().Equal(25m, 20m);
```

Run: `dotnet test tests/GymTracker.Core.Tests --filter PlateMathTests`
Expected: build error, `PerSide` and `PlateLoad` do not exist.

- [ ] **Step 2: Implement `PerSide`**

Add to `PlateMath.cs` (above the class):

```csharp
/// <summary>Plates for one side, heaviest first, and what the bar then weighs.</summary>
/// <param name="Exact">True when the total equals the target.</param>
/// <param name="BelowBar">True when the target is lighter than the empty bar.</param>
public record PlateLoad(IReadOnlyList<decimal> PerSide, decimal TotalKg, bool Exact, bool BelowBar);
```

and inside `PlateMath`:

```csharp
    // Plates are searched in 50 g steps, the smallest common plate step (0.5, 1.25 and 2.5 are all multiples).
    private const decimal Step = 0.05m;

    /// <summary>
    /// The heaviest load at or below the target, using the fewest plates (any number of pairs of each plate).
    /// An exact search, not greedy: plates {15, 10} make 20 per side as 10 + 10.
    /// </summary>
    public static PlateLoad PerSide(decimal targetKg, decimal barKg, IEnumerable<decimal> platesKg)
    {
        if (targetKg < barKg)
        {
            return new PlateLoad([], barKg, false, true);
        }

        var plates = platesKg
            .Where(p => p > 0 && p % Step == 0)
            .Distinct()
            .OrderDescending()
            .Select(p => (Kg: p, Units: (int)(p / Step)))
            .ToList();
        var goal = (int)Math.Floor((targetKg - barKg) / 2 / Step);

        // fewest[s] = fewest plates that make exactly s units per side; last[s] = the plate added to get there.
        var fewest = new int[goal + 1];
        var last = new int[goal + 1];
        Array.Fill(fewest, int.MaxValue);
        fewest[0] = 0;
        for (var s = 1; s <= goal; s++)
        {
            for (var i = 0; i < plates.Count; i++)
            {
                var rest = s - plates[i].Units;
                if (rest >= 0 && fewest[rest] != int.MaxValue && fewest[rest] + 1 < fewest[s])
                {
                    fewest[s] = fewest[rest] + 1;
                    last[s] = i;
                }
            }
        }

        var reach = goal;
        while (fewest[reach] == int.MaxValue)
        {
            reach--;
        }

        var perSide = new List<decimal>();
        for (var s = reach; s > 0; s -= plates[last[s]].Units)
        {
            perSide.Add(plates[last[s]].Kg);
        }

        perSide.Sort((a, b) => b.CompareTo(a));
        var total = barKg + 2 * perSide.Sum();
        return new PlateLoad(perSide, total, total == targetKg, false);
    }
```

Run: `dotnet test tests/GymTracker.Core.Tests --filter PlateMathTests`
Expected: all PlateMath tests pass.

- [ ] **Step 3: Write the failing endpoint tests**

Create `tests/GymTracker.Web.Tests/PlatesEndpointTests.cs`:

```csharp
using System.Net;
using FluentAssertions;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class PlatesEndpointTests(PostgresFixture fixture)
{
    private static async Task<(HttpClient Client, int SessionExerciseId)> SessionWithBenchAsync(AppFactory app)
    {
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/train");
        var start = await client.PostAsync("/train/start", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        var sessionUrl = start.Headers.Location!.ToString();
        var sessionId = sessionUrl.Split('/').Last();
        var picker = await client.GetStringAsync($"/train/sessions/{sessionId}/add-exercise");
        var benchId = System.Text.RegularExpressions.Regex.Match(picker, "name=\"exerciseId\" value=\"(\\d+)\"[^§]*?Bench press").Groups[1].Value;
        await client.PostAsync($"/train/sessions/{sessionId}/exercises", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["exerciseId"] = benchId,
            ["__RequestVerificationToken"] = await AppFactory.TokenAsync(client, sessionUrl),
        }));
        var page = await client.GetStringAsync(sessionUrl);
        var seId = int.Parse(System.Text.RegularExpressions.Regex.Match(page, "id=\"ex-(\\d+)\"").Groups[1].Value);
        return (client, seId);
    }

    [Fact]
    public async Task Plates_list_what_goes_on_each_side()
    {
        await using var app = new AppFactory(fixture);
        var (client, id) = await SessionWithBenchAsync(app);

        var html = await client.GetStringAsync($"/train/session-exercises/{id}/plates?weight=100");

        html.Should().Contain("data-plate=\"25\"").And.Contain("data-plate=\"15\"").And.Contain("data-total=\"100\"");
    }

    [Fact]
    public async Task Plates_without_a_weight_asks_for_one()
    {
        await using var app = new AppFactory(fixture);
        var (client, id) = await SessionWithBenchAsync(app);

        var response = await client.GetAsync($"/train/session-exercises/{id}/plates");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("data-plates-empty");
    }

    [Fact]
    public async Task Another_users_session_exercise_is_not_found()
    {
        await using var app = new AppFactory(fixture);
        var (_, id) = await SessionWithBenchAsync(app);
        var (stranger, _) = await app.SignedInClientAsync();

        (await stranger.GetAsync($"/train/session-exercises/{id}/plates?weight=100")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
```

Before running, check the regex for the bench id against the actual picker markup (`<input type="hidden" name="exerciseId" value="N" />` followed by the exercise name in the same `<form>`); adjust it if the attribute order differs. If `SignedInClientAsync` users lack a profile row, look at how `UserCreator` creates profiles; it does create one.

Run: `dotnet test tests/GymTracker.Web.Tests --filter PlatesEndpointTests`
Expected: FAIL, 404 for the plates URL.

- [ ] **Step 4: Endpoint, view model and partial**

In `SessionService.cs` add:

```csharp
    /// <summary>The weight of the last working set logged on this session exercise, or null.</summary>
    public Task<decimal?> LastWorkingWeightAsync(int sessionExerciseId, CancellationToken ct) =>
        db.Sets
            .Where(s => s.SessionExerciseId == sessionExerciseId && !s.IsWarmup)
            .OrderByDescending(s => s.SetNumber)
            .Select(s => (decimal?)s.WeightKg)
            .FirstOrDefaultAsync(ct);
```

In `ViewModels.cs` add:

```csharp
public record PlatesViewModel(int SessionExerciseId, decimal BarKg, decimal? TargetKg, PlateLoad? Load);
```

In `TrainController.cs` add:

```csharp
    /// <summary>The plate calculator sheet: the typed weight, else the last working set, else the suggestion.</summary>
    [HttpGet("session-exercises/{id:int}/plates")]
    public async Task<IActionResult> Plates(int id, decimal? weight, decimal? suggested, CancellationToken ct)
    {
        var exercise = await sessions.GetExerciseAsync(id, ct);
        var profile = await profiles.GetAsync(ct);
        var bar = PlateMath.BarFor(exercise.Exercise.Equipment, (await settings.GetAsync(exercise.ExerciseId, ct))?.BarWeightKg, profile.BarWeightKg)
            ?? throw new NotFoundException();
        var target = weight ?? await sessions.LastWorkingWeightAsync(id, ct) ?? suggested;
        var load = target is > 0 ? PlateMath.PerSide(target.Value, bar, profile.PlatesKg) : null;
        return PartialView("_Plates", new PlatesViewModel(id, bar, target, load));
    }
```

(`using GymTracker.Core.Training;` and `using GymTracker.Web.Features;` at the top.)

Create `src/GymTracker.Web/Views/Shared/_Plates.cshtml`:

```cshtml
@model PlatesViewModel
<div class="sheet-head"><span></span><span class="sheet-title" id="plates-title">@L["Plates"]</span><button type="button" class="sheet-close" data-sheet-close>@L["Done"]</button></div>
<div class="list">
    <div class="field-row">
        <label for="plates-weight">@L["Weight"]</label>
        <input id="plates-weight" class="num" type="number" name="weight" value="@(Model.TargetKg?.Kg())" min="0" max="500" step="any" inputmode="decimal"
               hx-get="/train/session-exercises/@Model.SessionExerciseId/plates" hx-trigger="input changed delay:300ms" hx-target="#plates-result" hx-select="#plates-result" hx-swap="outerHTML" />
        <span class="unit">@L["kg"]</span>
    </div>
</div>
<div id="plates-result" class="plates-result" aria-live="polite">
    @if (Model.Load is not { } load)
    {
        <p class="empty" data-plates-empty>@L["Enter a weight."]</p>
    }
    else if (load.BelowBar)
    {
        <p class="empty">@L["That's less than the bar ({0} kg).", Model.BarKg.Kg()]</p>
    }
    else
    {
        <p class="footnote">@L["Per side"]</p>
        <div class="plate-stack">
            @if (load.PerSide.Count == 0)
            {
                <span class="subhead">@L["Just the bar"]</span>
            }
            @foreach (var plate in load.PerSide)
            {
                <span class="plate num" data-plate="@plate.Kg()">@plate.Kg()</span>
            }
        </div>
        <p class="headline num" data-total="@load.TotalKg.Kg()">@L["{0} kg in total", load.TotalKg.Kg()]</p>
        @if (!load.Exact)
        {
            <p class="footnote">@L["Closest you can load: {0} kg", load.TotalKg.Kg()]</p>
        }
    }
</div>
```

- [ ] **Step 5: Card button, sheet, CSS**

In `_ExerciseCard.cshtml`, inside `<div class="ex-actions">` after the Warm-up button:

```cshtml
            @if (Model.BarKg is not null)
            {
                <button type="button" hx-get="/train/session-exercises/@Ex.Id/plates?suggested=@(Model.TargetFor(0)?.WeightKg.Kg())" hx-target="#plates-body" data-sheet-open="plates-sheet">@Ui.Icon("list")@L["Plates"]</button>
            }
```

In `Views/Train/Session.cshtml`, inside `@section Overlays` next to `_ExerciseSheet`:

```cshtml
    <div class="sheet" id="plates-sheet" hidden>
        <div class="sheet-scrim"></div>
        <div class="sheet-panel" role="dialog" aria-modal="true" aria-labelledby="plates-title">
            <div class="sheet-grabber" aria-hidden="true"></div>
            <div id="plates-body"></div>
        </div>
    </div>
```

In `app.css`:

```css
.plates-result { padding: var(--s-4) var(--s-1) 0; display: grid; gap: var(--s-2); }
.plate-stack { display: flex; flex-wrap: wrap; gap: var(--s-2); align-items: center; }
.plate { display: inline-grid; place-items: center; min-width: 52px; min-height: var(--tap); padding: 0 var(--s-3); border-radius: var(--r-md); background: var(--fill-strong); font-weight: var(--weight-semibold); }
```

Run: `dotnet test tests/GymTracker.Web.Tests --filter PlatesEndpointTests`
Expected: 3 passed.

- [ ] **Step 6: Translations, SPEC, browser check, commit**

Add to `strings.bg.json` (skip existing keys):

```json
  "{0} kg in total": "{0} кг общо",
  "Closest you can load: {0} kg": "Най-близкото, което може да се зареди: {0} кг",
  "Enter a weight.": "Въведете тегло.",
  "Just the bar": "Само лостът",
  "Per side": "От всяка страна",
  "Plates": "Дискове",
  "That's less than the bar ({0} kg).": "Това е по-малко от лоста ({0} кг).",
```

SPEC.md section 7, active session view: "Barbell exercises (or any with a bar weight set) have a Plates button: the plates per side for a weight, from the bar and plates in Settings."

Browser check at 390 px: Plates on Bench press opens the sheet with the suggested weight, typing 83 shows "Closest you can load: 82.5 kg", the ex-actions bar still fits three buttons.

Run `dotnet build` and `dotnet test`.

```bash
git add src tests SPEC.md
git commit -m "Add a plate calculator to barbell exercises"
```

---

### Task 4: Swap an exercise in a session

**Files:**
- Create: `src/GymTracker.Core/Training/ExerciseOrder.cs`
- Test: `tests/GymTracker.Core.Tests/ExerciseOrderTests.cs`
- Modify: `src/GymTracker.Web/Features/Training/SessionService.cs` (`SwapExerciseAsync`)
- Modify: `src/GymTracker.Web/Controllers/TrainController.cs` (two actions, inject `Localizer`)
- Modify: `src/GymTracker.Web/ViewModels/ViewModels.cs` (`ExercisePickerViewModel.FirstMuscle`)
- Modify: `src/GymTracker.Web/Views/Shared/ExercisePicker.cshtml` (or wherever the view lives; `TrainController` returns `View("ExercisePicker", ...)`)
- Modify: `src/GymTracker.Web/Views/Shared/_ExerciseCard.cshtml`
- Test: `tests/GymTracker.Web.Tests/SwapExerciseTests.cs`

**Interfaces:**
- Produces: `static IReadOnlyList<Exercise> ExerciseOrder.ForSwap(IEnumerable<Exercise> exercises, Exercise current)`.
- Produces: `Task<int> SessionService.SwapExerciseAsync(int sessionExerciseId, int exerciseId, CancellationToken ct)` returning the session id.
- Produces: `ExercisePickerViewModel(LocalText Title, string PostUrl, string BackUrl, IReadOnlyList<Exercise> Exercises, Muscle? FirstMuscle = null)`.
- Produces: `GET /train/session-exercises/{id}/swap`, `POST /train/session-exercises/{id}/swap` (form field `exerciseId`).

- [ ] **Step 1: Write the failing Core tests**

Create `tests/GymTracker.Core.Tests/ExerciseOrderTests.cs`:

```csharp
using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Training;

namespace GymTracker.Core.Tests;

public class ExerciseOrderTests
{
    private static Exercise Ex(int id, string name, Muscle muscle) => new() { Id = id, Name = name, PrimaryMuscle = muscle };

    [Fact]
    public void Same_muscle_comes_first_then_the_rest_by_muscle_and_name()
    {
        var bench = Ex(1, "Bench press", Muscle.Chest);
        var all = new[]
        {
            Ex(2, "Squat", Muscle.Quads),
            Ex(3, "Incline dumbbell press", Muscle.Chest),
            bench,
            Ex(4, "Barbell row", Muscle.Back),
            Ex(5, "Cable fly", Muscle.Chest),
        };

        ExerciseOrder.ForSwap(all, bench).Select(e => e.Id).Should().Equal(5, 3, 4, 2);
    }

    [Fact]
    public void The_current_exercise_is_left_out()
    {
        var bench = Ex(1, "Bench press", Muscle.Chest);
        ExerciseOrder.ForSwap([bench], bench).Should().BeEmpty();
    }
}
```

Run: `dotnet test tests/GymTracker.Core.Tests --filter ExerciseOrderTests`
Expected: build error.

- [ ] **Step 2: Implement `ExerciseOrder`**

Create `src/GymTracker.Core/Training/ExerciseOrder.cs`:

```csharp
using GymTracker.Core.Domain;

namespace GymTracker.Core.Training;

public static class ExerciseOrder
{
    /// <summary>Candidates to swap in: the same primary muscle first, then the rest grouped by muscle; each by name.</summary>
    public static IReadOnlyList<Exercise> ForSwap(IEnumerable<Exercise> exercises, Exercise current) =>
        exercises
            .Where(e => e.Id != current.Id)
            .OrderBy(e => e.PrimaryMuscle == current.PrimaryMuscle ? 0 : 1)
            .ThenBy(e => e.PrimaryMuscle)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
```

Run: `dotnet test tests/GymTracker.Core.Tests --filter ExerciseOrderTests`
Expected: 2 passed.

- [ ] **Step 3: Write the failing service tests**

Create `tests/GymTracker.Web.Tests/SwapExerciseTests.cs`:

```csharp
using FluentAssertions;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Training;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class SwapExerciseTests(PostgresFixture fixture)
{
    private async Task<(UserScope S, int TemplateId, int SessionExerciseId, int InclineId)> StartedAsync()
    {
        var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var bench = await s.Db.Exercises.FirstAsync(e => e.Name == "Bench press");
        var incline = await s.Db.Exercises.FirstAsync(e => e.Name == "Incline dumbbell press");
        var template = await s.Templates.CreateAsync("Push", default);
        await s.Templates.AddExerciseAsync(template.Id, bench.Id, default);
        var item = (await s.Templates.GetAsync(template.Id, default)).Exercises[0];
        await s.Templates.UpdateExerciseAsync(item.Id, new TemplateExerciseInput(4, 6, 10, 2.5m, 150), default);
        var session = await s.Sessions.GetAsync((await s.Sessions.StartAsync(template.Id, default)).Id, default);
        return (s, template.Id, session.Exercises[0].Id, incline.Id);
    }

    [Fact]
    public async Task Swapping_keeps_the_slot_and_leaves_the_template_alone()
    {
        var (s, templateId, seId, inclineId) = await StartedAsync();
        await using var _ = s;

        await s.Sessions.SwapExerciseAsync(seId, inclineId, default);

        s.Db.ChangeTracker.Clear();
        var item = await s.Sessions.GetExerciseAsync(seId, default);
        (item.ExerciseId, item.WorkingSets, item.RepMin, item.RepMax, item.RestSeconds).Should().Be((inclineId, 4, 6, 10, 150));
        (await s.Templates.GetAsync(templateId, default)).Exercises[0].Exercise.Name.Should().Be("Bench press");
    }

    [Fact]
    public async Task Swapping_is_refused_once_a_set_is_logged()
    {
        var (s, _, seId, inclineId) = await StartedAsync();
        await using var _ = s;
        await s.Sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), seId, 40, 10, null, true, null), default);

        await s.Invoking(x => x.Sessions.SwapExerciseAsync(seId, inclineId, default)).Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Another_users_session_exercise_is_not_found()
    {
        var (s, _, seId, inclineId) = await StartedAsync();
        await using var _ = s;
        await using var other = new UserScope(fixture, await fixture.CreateUserAsync());

        await other.Invoking(x => x.Sessions.SwapExerciseAsync(seId, inclineId, default)).Should().ThrowAsync<NotFoundException>();
    }
}
```

Run: `dotnet test tests/GymTracker.Web.Tests --filter SwapExerciseTests`
Expected: build error, no `SwapExerciseAsync`.

- [ ] **Step 4: Implement `SwapExerciseAsync`**

In `SessionService.cs`, after `AddExerciseAsync`:

```csharp
    /// <summary>
    /// Replaces the exercise in a session slot, keeping its sets, rep range, increment and rest. Only before any set
    /// is logged on it, so history never mixes two exercises. The template is not changed.
    /// </summary>
    public async Task<int> SwapExerciseAsync(int sessionExerciseId, int exerciseId, CancellationToken ct)
    {
        var item = await db.SessionExercises.Include(e => e.Sets).SingleOrDefaultAsync(e => e.Id == sessionExerciseId, ct)
            ?? throw new NotFoundException();
        if (item.Sets.Count > 0)
        {
            throw new ValidationException("Only an exercise with no sets logged can be swapped.");
        }

        var exercise = await db.Exercises.SingleOrDefaultAsync(e => e.Id == exerciseId && !e.IsArchived, ct)
            ?? throw new NotFoundException("Exercise not found.");
        item.ExerciseId = exercise.Id;
        await db.SaveChangesAsync(ct);
        return item.SessionId;
    }
```

Run: `dotnet test tests/GymTracker.Web.Tests --filter SwapExerciseTests`
Expected: 3 passed.

- [ ] **Step 5: Picker, actions, menu item**

In `ViewModels.cs` change the picker record to:

```csharp
public record ExercisePickerViewModel(GymTracker.Core.Text.LocalText Title, string PostUrl, string BackUrl, IReadOnlyList<Exercise> Exercises, Muscle? FirstMuscle = null);
```

In the picker view replace the group loop header with:

```cshtml
    @foreach (var group in Model.Exercises.GroupBy(e => e.PrimaryMuscle).OrderBy(g => g.Key == Model.FirstMuscle ? 0 : 1))
```

(`OrderBy` is stable, so the other groups keep the incoming order.)

In `TrainController.cs` inject `Localizer localizer` (`using GymTracker.Web.Features.Localization;`, `using GymTracker.Core.Text;`) and add:

```csharp
    [HttpGet("session-exercises/{id:int}/swap")]
    public async Task<IActionResult> Swap(int id, CancellationToken ct)
    {
        var item = await sessions.GetExerciseAsync(id, ct);
        return View("ExercisePicker", new ExercisePickerViewModel(
            new LocalText("Swap {0}", localizer.Exercise(item.Exercise)),
            $"/train/session-exercises/{id}/swap",
            $"/train/sessions/{item.SessionId}",
            ExerciseOrder.ForSwap(await exercises.ListAsync(ct), item.Exercise),
            item.Exercise.PrimaryMuscle));
    }

    [HttpPost("session-exercises/{id:int}/swap")]
    public async Task<IActionResult> Swap(int id, int exerciseId, CancellationToken ct) =>
        Redirect($"/train/sessions/{await sessions.SwapExerciseAsync(id, exerciseId, ct)}");
```

In `_ExerciseCard.cshtml`, in the menu after "Notes & bar":

```cshtml
                @if (Ex.Sets.Count == 0)
                {
                    <a href="/train/session-exercises/@Ex.Id/swap" role="menuitem">@L["Swap exercise"]@Ui.Icon("reorder")</a>
                }
```

- [ ] **Step 6: Translations, SPEC, browser check, commit**

Add to `strings.bg.json`:

```json
  "Only an exercise with no sets logged can be swapped.": "Може да се смени само упражнение без записани серии.",
  "Swap {0}": "Смяна на {0}",
  "Swap exercise": "Смени упражнението",
```

SPEC.md section 7, active session view: "Swap an exercise for another one in this session only, while it has no sets logged; same-muscle exercises are listed first."

Browser check at 390 px: on an exercise with no sets, Swap opens the picker with that muscle's group first; picking returns to the session with the new exercise and its own suggestion; the item is gone from the menu once a set is logged.

Run `dotnet build` and `dotnet test`.

```bash
git add src tests SPEC.md
git commit -m "Swap an exercise for this session only"
```

---

### Task 5: Quick add

**Files:**
- Modify: `src/GymTracker.Core/Domain/Nutrition.cs` (`EntrySource.Quick`)
- Modify: `src/GymTracker.Core/Nutrition/FoodMath.cs` (`ValidateQuick`)
- Modify: `tests/GymTracker.Core.Tests/NutritionTests.cs`
- Modify: `src/GymTracker.Web/Features/Food/FoodLogService.cs` (`AddQuickAsync`, `UpdateQuickAsync`, guards)
- Modify: `src/GymTracker.Web/Controllers/FoodController.cs` (three actions, inject `Localizer`)
- Create: `src/GymTracker.Web/Views/Food/_QuickAddForm.cshtml`
- Modify: `src/GymTracker.Web/Views/Food/Index.cshtml`, `src/GymTracker.Web/Views/Food/Entry.cshtml`
- Test: `tests/GymTracker.Web.Tests/QuickAddTests.cs`

**Interfaces:**
- Produces: `EntrySource.Quick` (appended last; stored as a string).
- Produces: `static LocalText? FoodMath.ValidateQuick(decimal kcal, decimal protein, decimal carbs, decimal fat)`.
- Produces: `FoodLogService.AddQuickAsync(DateOnly date, Meal meal, string description, decimal kcal, decimal protein, decimal carbs, decimal fat, CancellationToken)`, `FoodLogService.UpdateQuickAsync(int id, string description, Meal meal, decimal kcal, decimal protein, decimal carbs, decimal fat, CancellationToken)`.
- Produces: `GET /food/quick?date=&meal=` (partial), `POST /food/quick`, `POST /food/entries/{id}/quick`.

- [ ] **Step 1: Failing Core tests**

Append to `FoodMathTests` in `tests/GymTracker.Core.Tests/NutritionTests.cs`:

```csharp
    [Theory]
    [InlineData(650, 40, 0, 0, true)]
    [InlineData(1, 0, 0, 0, true)]
    [InlineData(5000, 500, 500, 500, true)]
    [InlineData(0, 10, 0, 0, false)]
    [InlineData(5001, 0, 0, 0, false)]
    [InlineData(500, -1, 0, 0, false)]
    [InlineData(500, 0, 501, 0, false)]
    public void Quick_entries_need_calories_and_sane_macros(decimal kcal, decimal p, decimal c, decimal f, bool ok) =>
        (FoodMath.ValidateQuick(kcal, p, c, f) is null).Should().Be(ok);
```

Run: `dotnet test tests/GymTracker.Core.Tests --filter FoodMathTests`
Expected: build error.

- [ ] **Step 2: Implement**

Append `Quick` to `EntrySource` with a doc comment: `/// <summary>Calories and macros typed directly, with no food and no grams.</summary>`.

In `FoodMath`:

```csharp
    /// <summary>Checks a quick entry (calories typed directly). Returns an error, or null when acceptable.</summary>
    public static LocalText? ValidateQuick(decimal kcal, decimal protein, decimal carbs, decimal fat)
    {
        if (kcal is < 1 or > 5000)
        {
            return "Calories must be between 1 and 5000.";
        }

        if (protein is < 0 or > 500 || carbs is < 0 or > 500 || fat is < 0 or > 500)
        {
            return "Protein, carbs and fat must be between 0 and 500 g.";
        }

        return null;
    }
```

Run: `dotnet test tests/GymTracker.Core.Tests --filter FoodMathTests`
Expected: pass.

- [ ] **Step 3: Failing service tests**

Create `tests/GymTracker.Web.Tests/QuickAddTests.cs`:

```csharp
using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Web.Features;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class QuickAddTests(PostgresFixture fixture)
{
    private static readonly DateOnly Day = new(2026, 10, 4);

    [Fact]
    public async Task A_quick_entry_counts_in_the_day_and_stays_out_of_recents()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());

        await s.FoodLog.AddQuickAsync(Day, Meal.Dinner, "Restaurant", 650, 40, 60, 25, default);

        var day = await s.FoodLog.DayAsync(Day, default);
        var entry = day.Entries.Single();
        (entry.Source, entry.Grams, entry.Kcal, entry.ProteinG, entry.FoodItemId).Should().Be((EntrySource.Quick, 0m, 650m, 40m, (int?)null));
        day.Totals.Kcal.Should().Be(650);
        (await s.FoodLog.RecentsAsync(Day, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Invalid_quick_entries_are_rejected()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.Invoking(x => x.FoodLog.AddQuickAsync(Day, Meal.Snack, "x", 0, 0, 0, 0, default)).Should().ThrowAsync<ValidationException>();
        await s.Invoking(x => x.FoodLog.AddQuickAsync(Day, Meal.Snack, "x", 100, 600, 0, 0, default)).Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Editing_a_quick_entry_changes_its_numbers_and_never_scales_by_grams()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.FoodLog.AddQuickAsync(Day, Meal.Lunch, "Wrap", 500, 30, 50, 15, default);
        var id = (await s.FoodLog.DayAsync(Day, default)).Entries.Single().Id;

        await s.FoodLog.UpdateQuickAsync(id, "Big wrap", Meal.Dinner, 700, 35, 70, 25, default);
        await s.Invoking(x => x.FoodLog.UpdateAsync(id, "Big wrap", 200, Meal.Dinner, default)).Should().ThrowAsync<ValidationException>();

        s.Db.ChangeTracker.Clear();
        var entry = await s.FoodLog.GetAsync(id, default);
        (entry.Description, entry.Meal, entry.Kcal, entry.ProteinG, entry.CarbsG, entry.FatG, entry.Grams).Should().Be(("Big wrap", Meal.Dinner, 700m, 35m, 70m, 25m, 0m));
    }

    [Fact]
    public async Task Copying_a_meal_keeps_quick_entries_quick()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.FoodLog.AddQuickAsync(Day, Meal.Breakfast, "Cafe", 300, 10, 40, 10, default);

        await s.FoodLog.CopyMealAsync(Day, Day.AddDays(1), Meal.Breakfast, default);

        (await s.FoodLog.DayAsync(Day.AddDays(1), default)).Entries.Single().Source.Should().Be(EntrySource.Quick);
    }
}
```

Run: `dotnet test tests/GymTracker.Web.Tests --filter QuickAddTests`
Expected: build error.

- [ ] **Step 4: Implement the service methods**

In `FoodLogService.cs` add:

```csharp
    /// <summary>Logs calories and macros typed directly, with no food item and no grams.</summary>
    public async Task AddQuickAsync(DateOnly date, Meal meal, string description, decimal kcal, decimal protein, decimal carbs, decimal fat, CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new InvalidOperationException("No signed-in user.");
        if (FoodMath.ValidateQuick(kcal, protein, carbs, fat) is { } error)
        {
            throw new ValidationException(error);
        }

        db.FoodLog.Add(new FoodLogEntry
        {
            UserId = userId,
            Date = date,
            Meal = meal,
            Description = CleanDescription(description),
            Grams = 0,
            Kcal = Math.Round(kcal, 1),
            ProteinG = Math.Round(protein, 1),
            CarbsG = Math.Round(carbs, 1),
            FatG = Math.Round(fat, 1),
            Source = EntrySource.Quick,
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateQuickAsync(int id, string description, Meal meal, decimal kcal, decimal protein, decimal carbs, decimal fat, CancellationToken ct)
    {
        var entry = await GetAsync(id, ct);
        if (entry.Source != EntrySource.Quick)
        {
            throw new ValidationException("Change this entry's amount instead.");
        }

        if (FoodMath.ValidateQuick(kcal, protein, carbs, fat) is { } error)
        {
            throw new ValidationException(error);
        }

        entry.Description = CleanDescription(description);
        entry.Meal = meal;
        (entry.Kcal, entry.ProteinG, entry.CarbsG, entry.FatG) = (Math.Round(kcal, 1), Math.Round(protein, 1), Math.Round(carbs, 1), Math.Round(fat, 1));
        await db.SaveChangesAsync(ct);
    }
```

At the top of `UpdateAsync`, after loading the entry:

```csharp
        if (entry.Source == EntrySource.Quick)
        {
            throw new ValidationException("Change a quick entry's calories instead of its amount.");
        }
```

In `CopyMealAsync` change the source line to:

```csharp
                Source = e.Source == EntrySource.Quick ? EntrySource.Quick : EntrySource.Recent,
```

Run: `dotnet test tests/GymTracker.Web.Tests --filter QuickAddTests`
Expected: 4 passed.

- [ ] **Step 5: Controller, sheet, rows, entry page**

In `FoodController.cs` inject `Localizer localizer` (`using GymTracker.Web.Features.Localization;` is already there) and add:

```csharp
    [HttpGet("quick")]
    public IActionResult QuickForm(DateOnly date, Meal meal) => PartialView("_QuickAddForm", (date, meal));

    [HttpPost("quick")]
    public async Task<IActionResult> Quick(DateOnly date, Meal meal, string? description, decimal kcal, decimal? protein, decimal? carbs, decimal? fat, CancellationToken ct)
    {
        await log.AddQuickAsync(date, meal, string.IsNullOrWhiteSpace(description) ? localizer["Quick add"] : description, kcal, protein ?? 0, carbs ?? 0, fat ?? 0, ct);
        return Redirect($"/food?date={date:yyyy-MM-dd}");
    }

    [HttpPost("entries/{id:int}/quick")]
    public async Task<IActionResult> UpdateQuick(int id, string description, Meal meal, decimal kcal, decimal? protein, decimal? carbs, decimal? fat, CancellationToken ct)
    {
        await log.UpdateQuickAsync(id, description, meal, kcal, protein ?? 0, carbs ?? 0, fat ?? 0, ct);
        var entry = await log.GetAsync(id, ct);
        return Redirect($"/food?date={entry.Date:yyyy-MM-dd}");
    }
```

Create `src/GymTracker.Web/Views/Food/_QuickAddForm.cshtml`:

```cshtml
@model (DateOnly Date, GymTracker.Core.Domain.Meal Meal)
<div class="sheet-head"><span></span><span class="sheet-title" id="quick-title">@L["Quick add"]</span><button type="button" class="sheet-close" data-sheet-close>@L["Cancel"]</button></div>
<form method="post" asp-antiforgery="true" action="/food/quick">
    <input type="hidden" name="date" value="@Model.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)" />
    <input type="hidden" name="meal" value="@Model.Meal" />
    <div class="list">
        <div class="field-row"><label for="quick-name">@L["Name"]</label><input id="quick-name" type="text" name="description" maxlength="200" placeholder="@L["Quick add"]" /></div>
        <div class="field-row"><label for="quick-kcal">@L["Calories"]</label><input id="quick-kcal" class="num" type="number" name="kcal" min="1" max="5000" step="any" inputmode="decimal" required autofocus /><span class="unit">@L["kcal"]</span></div>
        <div class="field-row"><label for="quick-protein">@L["Protein"]</label><input id="quick-protein" class="num" type="number" name="protein" min="0" max="500" step="any" inputmode="decimal" /><span class="unit">@L["g"]</span></div>
        <div class="field-row"><label for="quick-carbs">@L["Carbs"]</label><input id="quick-carbs" class="num" type="number" name="carbs" min="0" max="500" step="any" inputmode="decimal" /><span class="unit">@L["g"]</span></div>
        <div class="field-row"><label for="quick-fat">@L["Fat"]</label><input id="quick-fat" class="num" type="number" name="fat" min="0" max="500" step="any" inputmode="decimal" /><span class="unit">@L["g"]</span></div>
    </div>
    <button type="submit" class="btn btn-primary btn-block" style="margin-top: var(--s-4)">@L["Add"]</button>
</form>
```

In `Views/Food/Index.cshtml`:
- In each entry row, replace the `row-sub` span content with:

```cshtml
                        <span class="row-sub num" style="display: block">@(e.Source == EntrySource.Quick
                            ? L["{0} kcal · {1} g protein", Math.Round(e.Kcal), e.ProteinG.Kg(1)]
                            : L["{0} g · {1} kcal · {2} g protein", e.Grams.Kg(), Math.Round(e.Kcal), e.ProteinG.Kg(1)])</span>
```

- After the "Add to …" link row, add:

```cshtml
            <button type="button" class="row row-link" hx-get="/food/quick?date=@Iso(day.Date)&amp;meal=@meal" hx-target="#quick-body" data-sheet-open="quick-sheet" style="width: 100%; color: var(--accent-text); text-align: left">
                @Ui.Icon("plus", "icon-sm")<span class="row-main">@L["Quick add"]</span>
            </button>
```

- At the end add:

```cshtml
@section Overlays {
    <div class="sheet" id="quick-sheet" hidden>
        <div class="sheet-scrim"></div>
        <div class="sheet-panel" role="dialog" aria-modal="true" aria-labelledby="quick-title">
            <div class="sheet-grabber" aria-hidden="true"></div>
            <div id="quick-body"></div>
        </div>
    </div>
}
```

(If `EntrySource` isn't in scope in the view, use `GymTracker.Core.Domain.EntrySource.Quick`; check `_ViewImports.cshtml`.)

In `Views/Food/Entry.cshtml`, wrap the existing edit form in `@if (Model.Source != EntrySource.Quick) { ... } else { ... }` with this else branch:

```cshtml
    <form method="post" asp-antiforgery="true" action="/food/entries/@Model.Id/quick" class="section">
        <div class="list">
            <div class="field-row"><label for="description">@L["Description"]</label><input id="description" type="text" name="description" value="@Model.Description" maxlength="200" required /></div>
            <div class="field-row"><label for="kcal">@L["Calories"]</label><input id="kcal" class="num" type="number" name="kcal" value="@Model.Kcal.Kg()" min="1" max="5000" step="any" inputmode="decimal" required /><span class="unit">@L["kcal"]</span></div>
            <div class="field-row"><label for="protein">@L["Protein"]</label><input id="protein" class="num" type="number" name="protein" value="@Model.ProteinG.Kg()" min="0" max="500" step="any" inputmode="decimal" /><span class="unit">@L["g"]</span></div>
            <div class="field-row"><label for="carbs">@L["Carbs"]</label><input id="carbs" class="num" type="number" name="carbs" value="@Model.CarbsG.Kg()" min="0" max="500" step="any" inputmode="decimal" /><span class="unit">@L["g"]</span></div>
            <div class="field-row"><label for="fat">@L["Fat"]</label><input id="fat" class="num" type="number" name="fat" value="@Model.FatG.Kg()" min="0" max="500" step="any" inputmode="decimal" /><span class="unit">@L["g"]</span></div>
            <div class="field-row">
                <label for="meal">@L["Meal"]</label>
                <select id="meal" name="meal">
                    @foreach (var meal in Enum.GetValues<Meal>())
                    {
                        <option value="@meal" selected="@(meal == Model.Meal)">@L.Enum(meal)</option>
                    }
                </select>
            </div>
        </div>
        <button type="submit" class="btn btn-primary btn-block" style="margin-top: var(--s-5)">@L["Save"]</button>
    </form>
```

- [ ] **Step 6: Translations, SPEC, browser check, commit**

Add to `strings.bg.json` (skip existing):

```json
  "Calories": "Калории",
  "Calories must be between 1 and 5000.": "Калориите трябва да са между 1 и 5000.",
  "Change a quick entry's calories instead of its amount.": "При бързо добавяне променете калориите, а не количеството.",
  "Change this entry's amount instead.": "Променете количеството на този запис.",
  "Protein, carbs and fat must be between 0 and 500 g.": "Протеинът, въглехидратите и мазнините трябва да са между 0 и 500 г.",
  "Quick add": "Бързо добавяне",
```

SPEC.md section 4 (Nutrition): `EntrySource.Quick` entries have no food and grams 0. Section 7 (Food): "Quick add per meal: name, calories and optional macros; edited by their numbers, never scaled."

Browser check at 390 px: Food → Quick add under Lunch opens the sheet with the keyboard on calories; saving shows "650 kcal · 40 g protein"; the entry page shows the calorie fields.

Run `dotnet build` and `dotnet test`.

```bash
git add src tests SPEC.md
git commit -m "Quick-add calories and macros to a meal"
```

---

### Task 6: Saved meals

**Files:**
- Modify: `src/GymTracker.Core/Domain/Nutrition.cs` (`SavedMeal`, `SavedMealItem`)
- Modify: `src/GymTracker.Core/Nutrition/FoodMath.cs` (`Scale`)
- Modify: `tests/GymTracker.Core.Tests/NutritionTests.cs`
- Modify: `src/GymTracker.Web/Data/AppDbContext.cs`; migration `AddSavedMeals`
- Create: `src/GymTracker.Web/Features/Food/SavedMealService.cs`
- Create: `src/GymTracker.Web/Controllers/SavedMealsController.cs`
- Create: `src/GymTracker.Web/Views/Food/_SaveMealForm.cshtml`, `src/GymTracker.Web/Views/Food/_SavedMeal.cshtml`
- Modify: `src/GymTracker.Web/Views/Food/Index.cshtml`, `src/GymTracker.Web/Views/Food/Add.cshtml`, `src/GymTracker.Web/Controllers/FoodController.cs`, `src/GymTracker.Web/ViewModels/ViewModels.cs`, `src/GymTracker.Web/wwwroot/css/app.css`, `src/GymTracker.Web/Program.cs`, `tests/GymTracker.Web.Tests/UserScope.cs`
- Test: `tests/GymTracker.Web.Tests/SavedMealTests.cs`

**Interfaces:**
- Produces: `class SavedMeal { int Id; string UserId; string Name; DateTimeOffset CreatedAt; List<SavedMealItem> Items; const int MaxNameLength = 60; }`, `class SavedMealItem { int Id; int SavedMealId; int SortOrder; string Description; decimal Grams; decimal Kcal; decimal ProteinG; decimal CarbsG; decimal FatG; int? FoodItemId; EntrySource Source; }`.
- Produces: `static Totals FoodMath.Scale(Totals totals, decimal fromGrams, decimal toGrams)`.
- Produces: `SavedMealService`: `SaveFromMealAsync(DateOnly date, Meal meal, string name, CancellationToken) : Task<SavedMeal>`, `ListAsync(CancellationToken) : Task<List<SavedMeal>>`, `GetAsync(int id, CancellationToken) : Task<SavedMeal>`, `LogAsync(int id, DateOnly date, Meal meal, IReadOnlyList<decimal?> grams, CancellationToken) : Task<int>`, `RenameAsync(int id, string name, CancellationToken)`, `DeleteAsync(int id, CancellationToken)`.
- Produces: routes under `/food/saved-meals`: `GET new?date=&meal=` (partial), `POST ""`, `GET {id}?date=&meal=` (partial), `POST {id}/log`, `POST {id}/rename`, `POST {id}/delete`.
- Consumes: `EntrySource.Quick` (Task 5).

- [ ] **Step 1: Failing Core test**

Append to `FoodMathTests`:

```csharp
    [Fact]
    public void Scales_stored_totals_to_new_grams()
    {
        var totals = new Totals(240, 45, 0, 5.2m);
        FoodMath.Scale(totals, 200, 300).Should().Be(new Totals(360, 67.5m, 0, 7.8m));
        FoodMath.Scale(totals, 200, 200).Should().Be(totals);
    }

    [Fact]
    public void Quick_items_with_no_grams_keep_their_totals() =>
        FoodMath.Scale(new Totals(650, 40, 60, 25), 0, 150).Should().Be(new Totals(650, 40, 60, 25));
```

Run: `dotnet test tests/GymTracker.Core.Tests --filter FoodMathTests`
Expected: build error.

- [ ] **Step 2: Implement `Scale`**

In `FoodMath`:

```csharp
    /// <summary>Totals stored for one amount, rescaled to another. Items without grams (quick entries) are unchanged.</summary>
    public static Totals Scale(Totals totals, decimal fromGrams, decimal toGrams)
    {
        if (fromGrams <= 0)
        {
            return totals;
        }

        var f = toGrams / fromGrams;
        return new Totals(R(totals.Kcal * f), R(totals.ProteinG * f), R(totals.CarbsG * f), R(totals.FatG * f));
    }
```

Run: `dotnet test tests/GymTracker.Core.Tests --filter FoodMathTests`
Expected: pass.

- [ ] **Step 3: Domain, DbContext, migration**

Append to `src/GymTracker.Core/Domain/Nutrition.cs`:

```csharp
/// <summary>A named group of foods logged together often, copied from a logged meal.</summary>
public class SavedMeal
{
    public const int MaxNameLength = 60;

    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public List<SavedMealItem> Items { get; set; } = [];
}

/// <summary>A snapshot of one logged entry, with its stored totals, so later food edits don't change it.</summary>
public class SavedMealItem
{
    public int Id { get; set; }
    public int SavedMealId { get; set; }
    public SavedMeal SavedMeal { get; set; } = null!;
    public int SortOrder { get; set; }
    public string Description { get; set; } = "";

    /// <summary>0 for a quick entry, which has no amount.</summary>
    public decimal Grams { get; set; }

    public decimal Kcal { get; set; }
    public decimal ProteinG { get; set; }
    public decimal CarbsG { get; set; }
    public decimal FatG { get; set; }
    public int? FoodItemId { get; set; }
    public EntrySource Source { get; set; }
}
```

In `AppDbContext`:

```csharp
    public DbSet<SavedMeal> SavedMeals => Set<SavedMeal>();
    public DbSet<SavedMealItem> SavedMealItems => Set<SavedMealItem>();
```

```csharp
        builder.Entity<SavedMeal>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(SavedMeal.MaxNameLength);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId);
            e.HasQueryFilter(x => x.UserId == CurrentUserId);
        });

        builder.Entity<SavedMealItem>(e =>
        {
            e.HasOne(x => x.SavedMeal).WithMany(m => m.Items).HasForeignKey(x => x.SavedMealId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<FoodItem>().WithMany().HasForeignKey(x => x.FoodItemId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.Description).HasMaxLength(200);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Grams).HasPrecision(7, 1);
            e.Property(x => x.Kcal).HasPrecision(7, 1);
            e.Property(x => x.ProteinG).HasPrecision(6, 1);
            e.Property(x => x.CarbsG).HasPrecision(6, 1);
            e.Property(x => x.FatG).HasPrecision(6, 1);
            e.HasQueryFilter(x => x.SavedMeal.UserId == CurrentUserId);
        });
```

Run: `dotnet ef migrations add AddSavedMeals --project src/GymTracker.Web --output-dir Data/Migrations`

- [ ] **Step 4: Failing service tests**

In `UserScope` add `SavedMeals = new GymTracker.Web.Features.Food.SavedMealService(Db, currentUser, TimeProvider.System);` and `public GymTracker.Web.Features.Food.SavedMealService SavedMeals { get; }`.

Create `tests/GymTracker.Web.Tests/SavedMealTests.cs`:

```csharp
using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Food;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class SavedMealTests(PostgresFixture fixture)
{
    private static readonly DateOnly Day = new(2026, 10, 4);

    private static async Task<UserScope> WithBreakfastAsync(PostgresFixture fixture)
    {
        var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.FoodLog.AddAsync(
        [
            new NewEntry(Day, Meal.Breakfast, "Oats", 80, new Per100g(380, 13, 60, 7), null, EntrySource.Manual),
            new NewEntry(Day, Meal.Breakfast, "Milk", 250, new Per100g(64, 3.4m, 4.8m, 3.6m), null, EntrySource.Manual),
            new NewEntry(Day, Meal.Lunch, "Rice", 200, new Per100g(130, 2.7m, 28, 0.3m), null, EntrySource.Manual),
        ], default);
        await s.FoodLog.AddQuickAsync(Day, Meal.Breakfast, "Coffee out", 120, 4, 12, 6, default);
        return s;
    }

    [Fact]
    public async Task Saving_copies_only_that_meal_of_that_day()
    {
        await using var s = await WithBreakfastAsync(fixture);

        var saved = await s.SavedMeals.SaveFromMealAsync(Day, Meal.Breakfast, "Usual breakfast", default);

        s.Db.ChangeTracker.Clear();
        var loaded = await s.SavedMeals.GetAsync(saved.Id, default);
        loaded.Items.Select(i => (i.Description, i.Grams)).Should().Equal(("Oats", 80m), ("Milk", 250m), ("Coffee out", 0m));
    }

    [Fact]
    public async Task Logging_scales_each_item_to_its_new_grams()
    {
        await using var s = await WithBreakfastAsync(fixture);
        var saved = await s.SavedMeals.SaveFromMealAsync(Day, Meal.Breakfast, "Usual breakfast", default);
        var next = Day.AddDays(1);

        var count = await s.SavedMeals.LogAsync(saved.Id, next, Meal.Snack, [100m, null, 999m], default);

        count.Should().Be(3);
        var entries = (await s.FoodLog.DayAsync(next, default)).Entries;
        entries.Should().OnlyContain(e => e.Meal == Meal.Snack);
        entries.Single(e => e.Description == "Oats").Kcal.Should().Be(380);
        entries.Single(e => e.Description == "Milk").Grams.Should().Be(250);
        var coffee = entries.Single(e => e.Description == "Coffee out");
        (coffee.Source, coffee.Grams, coffee.Kcal).Should().Be((EntrySource.Quick, 0m, 120m));
    }

    [Fact]
    public async Task An_empty_meal_cannot_be_saved()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.Invoking(x => x.SavedMeals.SaveFromMealAsync(Day, Meal.Dinner, "Nothing", default)).Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Logging_drops_a_food_link_that_is_no_longer_visible()
    {
        await using var s = await WithBreakfastAsync(fixture);
        var saved = await s.SavedMeals.SaveFromMealAsync(Day, Meal.Breakfast, "Usual breakfast", default);
        // Point an item at a food this user can't see (another user's private item).
        await using var other = new UserScope(fixture, await fixture.CreateUserAsync());
        var hidden = new FoodItem { Name = "Private oats", OwnerUserId = other.UserId, KcalPer100g = 380, Source = FoodSource.Manual };
        other.Db.FoodItems.Add(hidden);
        await other.Db.SaveChangesAsync();
        await s.Db.SavedMealItems.Where(i => i.SavedMealId == saved.Id && i.Description == "Oats")
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.FoodItemId, hidden.Id));

        await s.SavedMeals.LogAsync(saved.Id, Day.AddDays(2), Meal.Breakfast, [null, null, null], default);

        (await s.FoodLog.DayAsync(Day.AddDays(2), default)).Entries.Single(e => e.Description == "Oats").FoodItemId.Should().BeNull();
    }

    [Fact]
    public async Task Another_users_saved_meal_is_not_found()
    {
        await using var s = await WithBreakfastAsync(fixture);
        var saved = await s.SavedMeals.SaveFromMealAsync(Day, Meal.Breakfast, "Mine", default);
        await using var other = new UserScope(fixture, await fixture.CreateUserAsync());

        await other.Invoking(x => x.SavedMeals.LogAsync(saved.Id, Day, Meal.Lunch, [], default)).Should().ThrowAsync<NotFoundException>();
        await other.Invoking(x => x.SavedMeals.DeleteAsync(saved.Id, default)).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Names_are_checked_and_renamed()
    {
        await using var s = await WithBreakfastAsync(fixture);
        await s.Invoking(x => x.SavedMeals.SaveFromMealAsync(Day, Meal.Breakfast, "  ", default)).Should().ThrowAsync<ValidationException>();
        var saved = await s.SavedMeals.SaveFromMealAsync(Day, Meal.Breakfast, "A", default);

        await s.SavedMeals.RenameAsync(saved.Id, " Breakfast B ", default);
        await s.Invoking(x => x.SavedMeals.RenameAsync(saved.Id, new string('x', 61), default)).Should().ThrowAsync<ValidationException>();

        s.Db.ChangeTracker.Clear();
        (await s.SavedMeals.GetAsync(saved.Id, default)).Name.Should().Be("Breakfast B");
    }
}
```

The `999m` grams in the logging test is for the quick item, which ignores grams; check that the order of `grams` matches `Items` ordered by `SortOrder`.

Run: `dotnet test tests/GymTracker.Web.Tests --filter SavedMealTests`
Expected: build error.

- [ ] **Step 5: Implement `SavedMealService`**

Create `src/GymTracker.Web/Features/Food/SavedMealService.cs`:

```csharp
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Food;

public class SavedMealService(AppDbContext db, ICurrentUser currentUser, TimeProvider time)
{
    public Task<List<SavedMeal>> ListAsync(CancellationToken ct) =>
        db.SavedMeals.AsNoTracking().Include(m => m.Items.OrderBy(i => i.SortOrder)).OrderBy(m => m.Name).ToListAsync(ct);

    public async Task<SavedMeal> GetAsync(int id, CancellationToken ct) =>
        await db.SavedMeals.Include(m => m.Items.OrderBy(i => i.SortOrder)).SingleOrDefaultAsync(m => m.Id == id, ct)
        ?? throw new NotFoundException();

    /// <summary>Copies one meal of one day into a new saved meal.</summary>
    public async Task<SavedMeal> SaveFromMealAsync(DateOnly date, Meal meal, string name, CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new InvalidOperationException("No signed-in user.");
        var clean = CleanName(name);
        var entries = await db.FoodLog.Where(e => e.Date == date && e.Meal == meal).OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync(ct);
        if (entries.Count == 0)
        {
            throw new ValidationException("This meal has nothing logged yet.");
        }

        var saved = new SavedMeal
        {
            UserId = userId,
            Name = clean,
            CreatedAt = time.GetUtcNow(),
            Items = entries.Select((e, i) => new SavedMealItem
            {
                SortOrder = i,
                Description = e.Description,
                Grams = e.Grams,
                Kcal = e.Kcal,
                ProteinG = e.ProteinG,
                CarbsG = e.CarbsG,
                FatG = e.FatG,
                FoodItemId = e.FoodItemId,
                Source = e.Source,
            }).ToList(),
        };
        db.SavedMeals.Add(saved);
        await db.SaveChangesAsync(ct);
        return saved;
    }

    /// <summary>
    /// Logs every item into a meal, each at its grams from <paramref name="grams"/> (same order as the items; null keeps
    /// the saved amount). Quick items keep their numbers. Returns how many entries were added.
    /// </summary>
    public async Task<int> LogAsync(int id, DateOnly date, Meal meal, IReadOnlyList<decimal?> grams, CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new InvalidOperationException("No signed-in user.");
        var saved = await GetAsync(id, ct);
        var foodIds = saved.Items.Where(i => i.FoodItemId != null).Select(i => i.FoodItemId!.Value).ToList();
        var visible = (await db.FoodItems.Where(f => foodIds.Contains(f.Id)).Select(f => f.Id).ToListAsync(ct)).ToHashSet();
        var now = time.GetUtcNow();

        for (var i = 0; i < saved.Items.Count; i++)
        {
            var item = saved.Items[i];
            var quick = item.Grams <= 0;
            var to = quick ? 0 : grams.ElementAtOrDefault(i) ?? item.Grams;
            if (!quick && (to <= 0 || to > FoodMath.MaxGrams))
            {
                throw new ValidationException(new GymTracker.Core.Text.LocalText("{0}: {1}", item.Description, FoodMath.Validate(to, new Per100g(0, 0, 0, 0))!));
            }

            var totals = FoodMath.Scale(new Totals(item.Kcal, item.ProteinG, item.CarbsG, item.FatG), item.Grams, to);
            db.FoodLog.Add(new FoodLogEntry
            {
                UserId = userId,
                Date = date,
                Meal = meal,
                Description = item.Description,
                Grams = Math.Round(to, 1),
                Kcal = totals.Kcal,
                ProteinG = totals.ProteinG,
                CarbsG = totals.CarbsG,
                FatG = totals.FatG,
                FoodItemId = item.FoodItemId is { } f && visible.Contains(f) ? f : null,
                Source = quick ? EntrySource.Quick : EntrySource.Recent,
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
        return saved.Items.Count;
    }

    public async Task RenameAsync(int id, string name, CancellationToken ct)
    {
        var saved = await GetAsync(id, ct);
        saved.Name = CleanName(name);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        var saved = await GetAsync(id, ct);
        db.SavedMeals.Remove(saved);
        await db.SaveChangesAsync(ct);
    }

    private static string CleanName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > SavedMeal.MaxNameLength)
        {
            throw new ValidationException("Name must be 1 to 60 characters.");
        }

        return trimmed;
    }
}
```

Register in `Program.cs` after `FoodLogService`: `builder.Services.AddScoped<SavedMealService>();`

Run: `dotnet test tests/GymTracker.Web.Tests --filter SavedMealTests`
Expected: 6 passed.

- [ ] **Step 6: Controller and partials**

Create `src/GymTracker.Web/Controllers/SavedMealsController.cs`:

```csharp
using GymTracker.Core.Domain;
using GymTracker.Core.Text;
using GymTracker.Web.Features.Food;
using GymTracker.Web.Features.Localization;
using GymTracker.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace GymTracker.Web.Controllers;

[Route("food/saved-meals")]
public class SavedMealsController(SavedMealService saved, Localizer localizer) : Controller
{
    [HttpGet("new")]
    public IActionResult New(DateOnly date, Meal meal) =>
        PartialView("_SaveMealForm", new SaveMealFormViewModel(date, meal, localizer.Enum(meal)));

    [HttpPost("")]
    public async Task<IActionResult> Create(DateOnly date, Meal meal, string name, CancellationToken ct)
    {
        await saved.SaveFromMealAsync(date, meal, name, ct);
        TempData.Notice("Saved as a meal.");
        return Redirect($"/food?date={date:yyyy-MM-dd}");
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Show(int id, DateOnly date, Meal meal, CancellationToken ct) =>
        PartialView("_SavedMeal", new SavedMealViewModel(await saved.GetAsync(id, ct), date, meal));

    [HttpPost("{id:int}/log")]
    public async Task<IActionResult> Log(int id, DateOnly date, Meal meal, List<decimal?> grams, CancellationToken ct)
    {
        var count = await saved.LogAsync(id, date, meal, grams, ct);
        TempData.Notice(LocalText.Count(count, "Added 1 item.", "Added {0} items."));
        return Redirect($"/food?date={date:yyyy-MM-dd}");
    }

    [HttpPost("{id:int}/rename")]
    public async Task<IActionResult> Rename(int id, string name, DateOnly date, Meal meal, CancellationToken ct)
    {
        await saved.RenameAsync(id, name, ct);
        return Redirect($"/food/add?date={date:yyyy-MM-dd}&meal={meal}&tab=recent");
    }

    [HttpPost("{id:int}/delete")]
    public async Task<IActionResult> Delete(int id, DateOnly date, Meal meal, CancellationToken ct)
    {
        await saved.DeleteAsync(id, ct);
        return Redirect($"/food/add?date={date:yyyy-MM-dd}&meal={meal}&tab=recent");
    }
}
```

In `ViewModels.cs`:

```csharp
public record SaveMealFormViewModel(DateOnly Date, Meal Meal, string DefaultName);

public record SavedMealViewModel(SavedMeal Meal, DateOnly Date, Meal LogTo);
```

and add `IReadOnlyList<SavedMeal> SavedMeals` as the last parameter of `FoodAddViewModel`. Update every `new FoodAddViewModel(...)` in `FoodController` to pass `[]`, except `Add`, which passes `tab == "recent" ? await savedMeals.ListAsync(ct) : []` (inject `SavedMealService savedMeals` into `FoodController`).

Create `src/GymTracker.Web/Views/Food/_SaveMealForm.cshtml`:

```cshtml
@model SaveMealFormViewModel
<div class="sheet-head"><span></span><span class="sheet-title" id="food-sheet-title">@L["Save as a meal"]</span><button type="button" class="sheet-close" data-sheet-close>@L["Cancel"]</button></div>
<form method="post" asp-antiforgery="true" action="/food/saved-meals">
    <input type="hidden" name="date" value="@Model.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)" />
    <input type="hidden" name="meal" value="@Model.Meal" />
    <div class="list">
        <div class="field-row"><label for="meal-name">@L["Name"]</label><input id="meal-name" type="text" name="name" value="@Model.DefaultName" maxlength="60" required /></div>
    </div>
    <button type="submit" class="btn btn-primary btn-block" style="margin-top: var(--s-4)">@L["Save"]</button>
</form>
```

Create `src/GymTracker.Web/Views/Food/_SavedMeal.cshtml`:

```cshtml
@model SavedMealViewModel
@{
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    var date = Model.Date.ToString("yyyy-MM-dd", inv);
}
<div class="sheet-head"><span></span><span class="sheet-title" id="food-sheet-title">@Model.Meal.Name</span><button type="button" class="sheet-close" data-sheet-close>@L["Cancel"]</button></div>
<form method="post" asp-antiforgery="true" action="/food/saved-meals/@Model.Meal.Id/log">
    <input type="hidden" name="date" value="@date" />
    <input type="hidden" name="meal" value="@Model.LogTo" />
    <div class="list">
        @foreach (var item in Model.Meal.Items)
        {
            @if (item.Grams > 0)
            {
                <div class="field-row"><label for="g-@item.Id">@item.Description</label><input id="g-@item.Id" class="num" type="number" name="grams" value="@item.Grams.Kg()" min="1" max="3000" step="any" inputmode="decimal" required /><span class="unit">@L["g"]</span></div>
            }
            else
            {
                <input type="hidden" name="grams" value="" />
                <div class="row"><span class="row-main">@item.Description</span><span class="row-value num">@L["{0} kcal", Math.Round(item.Kcal)]</span></div>
            }
        }
    </div>
    <button type="submit" class="btn btn-primary btn-block" style="margin-top: var(--s-4)">@L[AddTo[Model.LogTo]]</button>
</form>
<form method="post" asp-antiforgery="true" action="/food/saved-meals/@Model.Meal.Id/rename" class="section">
    <input type="hidden" name="date" value="@date" />
    <input type="hidden" name="meal" value="@Model.LogTo" />
    <div class="list">
        <div class="field-row"><label for="rename-meal">@L["Name"]</label><input id="rename-meal" type="text" name="name" value="@Model.Meal.Name" maxlength="60" required /><button type="submit" class="btn btn-plain">@L["Rename"]</button></div>
    </div>
</form>
<form method="post" asp-antiforgery="true" action="/food/saved-meals/@Model.Meal.Id/delete" data-confirm="@L["Delete this saved meal?"]" style="margin-top: var(--s-3)">
    <input type="hidden" name="date" value="@date" />
    <input type="hidden" name="meal" value="@Model.LogTo" />
    <button type="submit" class="btn btn-destructive btn-block">@L["Delete saved meal"]</button>
</form>

@functions {
    private static readonly Dictionary<GymTracker.Core.Domain.Meal, string> AddTo = new()
    {
        [GymTracker.Core.Domain.Meal.Breakfast] = "Add to breakfast",
        [GymTracker.Core.Domain.Meal.Lunch] = "Add to lunch",
        [GymTracker.Core.Domain.Meal.Dinner] = "Add to dinner",
        [GymTracker.Core.Domain.Meal.Snack] = "Add a snack",
    };
}
```

Model binding note: an empty `name="grams"` value binds to `null` in `List<decimal?>`, keeping positions aligned with the items. Confirm in the browser check that a quick item in the middle doesn't shift the grams of the items after it; if it does, switch to indexed names `grams[@i]`.

- [ ] **Step 7: Food day menu, Recent tab, sheet, CSS**

In `Views/Food/Index.cshtml`, replace the meal heading line with:

```cshtml
        <div class="meal-head">
            <h2 class="section-title">@L.Enum(meal)<span class="section-aside num">@(entries.Count > 0 ? L["{0} kcal", Math.Round(mealTotals.Kcal)] : "")</span></h2>
            @if (entries.Count > 0)
            {
                <div class="menu-wrap">
                    <button type="button" class="icon-btn" data-menu-trigger aria-expanded="false" aria-label="@L["Actions for {0}", L.Enum(meal)]">@Ui.Icon("more")</button>
                    <div class="menu" role="menu">
                        <button type="button" role="menuitem" hx-get="/food/saved-meals/new?date=@Iso(day.Date)&amp;meal=@meal" hx-target="#food-sheet-body" data-sheet-open="food-sheet">@L["Save as a meal"]@Ui.Icon("list")</button>
                    </div>
                </div>
            }
        </div>
```

and in the Overlays section add a second sheet:

```cshtml
    <div class="sheet" id="food-sheet" hidden>
        <div class="sheet-scrim"></div>
        <div class="sheet-panel" role="dialog" aria-modal="true" aria-labelledby="food-sheet-title">
            <div class="sheet-grabber" aria-hidden="true"></div>
            <div id="food-sheet-body"></div>
        </div>
    </div>
```

In `Views/Food/Add.cshtml`, in `case "recent":`, before the `@if (Model.Recents.Count == 0)` block:

```cshtml
        @if (Model.SavedMeals.Count > 0)
        {
            <section class="section" style="margin-top: 0">
                <h2 class="section-header">@L["Saved meals"]</h2>
                <div class="list">
                    @foreach (var m in Model.SavedMeals)
                    {
                        <button type="button" class="row row-link" style="width: 100%; text-align: left" hx-get="/food/saved-meals/@m.Id?date=@date&amp;meal=@Model.Meal" hx-target="#food-sheet-body" data-sheet-open="food-sheet">
                            <span class="row-main">
                                <span class="row-title" style="display: block">@m.Name</span>
                                <span class="row-sub num" style="display: block">@L.Count(m.Items.Count, "1 item", "{0} items") · @L["{0} kcal", Math.Round(m.Items.Sum(i => i.Kcal))]</span>
                            </span>
                            @Ui.Icon("chevron", "row-chevron")
                        </button>
                    }
                </div>
            </section>
            <h2 class="section-header" style="margin-top: var(--s-6)">@L["Recent"]</h2>
        }
```

and add to its `@section Overlays` (create the section's sheet next to the scanner's `@if`, outside it) the same `#food-sheet` markup as above.

In `app.css`:

```css
.meal-head { display: flex; align-items: center; gap: var(--s-2); margin-bottom: var(--s-2); }
.meal-head .section-title { flex: 1; margin-bottom: 0; }
```

- [ ] **Step 8: Translations, SPEC, browser check, commit**

Add to `strings.bg.json` (skip existing):

```json
  "{0} items": "{0} продукта",
  "1 item": "1 продукт",
  "Added 1 item.": "Добавен е 1 продукт.",
  "Added {0} items.": "Добавени са {0} продукта.",
  "Delete saved meal": "Изтрий запазеното ястие",
  "Delete this saved meal?": "Да се изтрие ли това запазено ястие?",
  "Name must be 1 to 60 characters.": "Името трябва да е от 1 до 60 знака.",
  "Save as a meal": "Запази като ястие",
  "Saved as a meal.": "Запазено като ястие.",
  "Saved meals": "Запазени ястия",
  "This meal has nothing logged yet.": "В това хранене още няма нищо записано.",
```

SPEC.md section 4 (Nutrition): `SavedMeal` and `SavedMealItem` (snapshots with stored totals). Section 7 (Food): "Save a logged meal under a name from the meal's menu; saved meals are listed first on the Recent tab and logged with editable grams."

Browser check at 390 px: save Breakfast as a meal from its menu, see the success banner; Add → Recent lists it; the sheet logs it into Snack with changed grams; rename and delete work; a meal with a quick entry in the middle logs every item at the right grams.

Run `dotnet build` and `dotnet test`.

```bash
git add src tests SPEC.md
git commit -m "Save a logged meal and log it again from Recent"
```

---

### Task 7: Soft delete and the undo service

**Files:**
- Modify: `src/GymTracker.Core/Domain/Training.cs`, `src/GymTracker.Core/Domain/Nutrition.cs` (`DeletedAt`)
- Modify: `src/GymTracker.Web/Data/AppDbContext.cs` (named filters); migration `AddSoftDelete`
- Modify: `src/GymTracker.Web/Features/Food/FoodLogService.cs`, `src/GymTracker.Web/Features/Training/SessionService.cs`, `src/GymTracker.Web/Features/Training/TemplateService.cs` (deletes set `DeletedAt`)
- Create: `src/GymTracker.Web/Features/Undo/UndoService.cs`, `src/GymTracker.Web/Features/Undo/SoftDeletePurge.cs`
- Modify: `src/GymTracker.Web/Program.cs`, `tests/GymTracker.Web.Tests/UserScope.cs`
- Test: `tests/GymTracker.Web.Tests/UndoTests.cs`

**Interfaces:**
- Produces: `DeletedAt` (`DateTimeOffset?`) on `FoodLogEntry`, `SetEntry`, `SessionExercise`, `TemplateExercise`.
- Produces: `AppDbContext.OwnerFilter = "Owner"`, `AppDbContext.SoftDeleteFilter = "SoftDelete"`.
- Produces: `enum UndoKind { Entry, Set, SessionExercise, TemplateExercise }`; `UndoService.RestoreAsync(UndoKind kind, int id, CancellationToken)`; `UndoService.Window` (10 minutes).
- Produces: `SoftDeletePurge` hosted service; `static Task<int> SoftDeletePurge.PurgeAsync(AppDbContext db, DateTimeOffset cutoff, CancellationToken ct)`.
- Changes: `TemplateService(AppDbContext db, ICurrentUser currentUser, TimeProvider time)` (adds `TimeProvider`).

- [ ] **Step 1: Failing tests**

In `UserScope`: construct `Templates = new TemplateService(Db, currentUser, TimeProvider.System);` and add `Undo = new GymTracker.Web.Features.Undo.UndoService(Db, TimeProvider.System);` with `public GymTracker.Web.Features.Undo.UndoService Undo { get; }`.

Create `tests/GymTracker.Web.Tests/UndoTests.cs`:

```csharp
using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Data;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Food;
using GymTracker.Web.Features.Training;
using GymTracker.Web.Features.Undo;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class UndoTests(PostgresFixture fixture)
{
    private static readonly DateOnly Day = new(2026, 10, 4);

    private async Task<(UserScope S, int SessionExerciseId)> SessionAsync()
    {
        var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var bench = await s.Db.Exercises.FirstAsync(e => e.Name == "Bench press");
        var template = await s.Templates.CreateAsync("Push", default);
        await s.Templates.AddExerciseAsync(template.Id, bench.Id, default);
        var session = await s.Sessions.GetAsync((await s.Sessions.StartAsync(template.Id, default)).Id, default);
        return (s, session.Exercises[0].Id);
    }

    [Fact]
    public async Task A_deleted_entry_leaves_the_day_and_comes_back_on_undo()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.FoodLog.AddQuickAsync(Day, Meal.Lunch, "Wrap", 500, 30, 50, 15, default);
        var id = (await s.FoodLog.DayAsync(Day, default)).Entries.Single().Id;

        await s.FoodLog.DeleteAsync(id, default);
        (await s.FoodLog.DayAsync(Day, default)).Totals.Kcal.Should().Be(0);

        await s.Undo.RestoreAsync(UndoKind.Entry, id, default);
        (await s.FoodLog.DayAsync(Day, default)).Totals.Kcal.Should().Be(500);
    }

    [Fact]
    public async Task A_removed_exercise_hides_its_sets_from_history_until_undone()
    {
        var (s, seId) = await SessionAsync();
        await using var _ = s;
        await s.Sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), seId, 80, 8, null, false, null), default);

        await s.Sessions.RemoveExerciseAsync(seId, default);
        s.Db.ChangeTracker.Clear();
        (await s.Db.Sets.CountAsync(x => x.SessionExerciseId == seId)).Should().Be(0);

        await s.Undo.RestoreAsync(UndoKind.SessionExercise, seId, default);
        s.Db.ChangeTracker.Clear();
        (await s.Db.Sets.CountAsync(x => x.SessionExerciseId == seId)).Should().Be(1);
    }

    [Fact]
    public async Task Restoring_a_set_renumbers_its_siblings()
    {
        var (s, seId) = await SessionAsync();
        await using var _ = s;
        var first = await s.Sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), seId, 80, 8, null, false, null), default);
        var second = await s.Sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), seId, 80, 8, null, false, null), default);
        await s.Sessions.DeleteSetAsync(second.Id, default);
        await s.Sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), seId, 82.5m, 6, null, false, null), default);

        await s.Undo.RestoreAsync(UndoKind.Set, second.Id, default);

        s.Db.ChangeTracker.Clear();
        (await s.Db.Sets.Where(x => x.SessionExerciseId == seId).OrderBy(x => x.SetNumber).Select(x => x.SetNumber).ToListAsync())
            .Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Restoring_a_template_exercise_keeps_sort_orders_unique()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var names = new[] { "Bench press", "Barbell row", "Squat" };
        var template = await s.Templates.CreateAsync("Full", default);
        foreach (var n in names)
        {
            await s.Templates.AddExerciseAsync(template.Id, (await s.Db.Exercises.FirstAsync(e => e.Name == n)).Id, default);
        }

        var middle = (await s.Templates.GetAsync(template.Id, default)).Exercises[1].Id;
        await s.Templates.RemoveExerciseAsync(middle, default);
        await s.Templates.AddExerciseAsync(template.Id, (await s.Db.Exercises.FirstAsync(e => e.Name == "Deadlift")).Id, default);

        await s.Undo.RestoreAsync(UndoKind.TemplateExercise, middle, default);

        s.Db.ChangeTracker.Clear();
        var orders = (await s.Templates.GetAsync(template.Id, default)).Exercises.Select(e => e.SortOrder).ToList();
        orders.Should().OnlyHaveUniqueItems().And.HaveCount(4);
    }

    [Fact]
    public async Task Undo_is_idempotent()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.FoodLog.AddQuickAsync(Day, Meal.Lunch, "Wrap", 500, 30, 50, 15, default);
        var id = (await s.FoodLog.DayAsync(Day, default)).Entries.Single().Id;
        await s.FoodLog.DeleteAsync(id, default);

        await s.Undo.RestoreAsync(UndoKind.Entry, id, default);
        await s.Undo.RestoreAsync(UndoKind.Entry, id, default);

        (await s.FoodLog.DayAsync(Day, default)).Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task Undo_after_ten_minutes_is_refused()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.FoodLog.AddQuickAsync(Day, Meal.Lunch, "Wrap", 500, 30, 50, 15, default);
        var id = (await s.FoodLog.DayAsync(Day, default)).Entries.Single().Id;
        await s.FoodLog.DeleteAsync(id, default);
        await s.Db.FoodLog.IgnoreQueryFilters([AppDbContext.SoftDeleteFilter]).Where(e => e.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.DeletedAt, DateTimeOffset.UtcNow.AddMinutes(-11)));

        await s.Invoking(x => x.Undo.RestoreAsync(UndoKind.Entry, id, default)).Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Undo_of_another_users_row_is_not_found()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.FoodLog.AddQuickAsync(Day, Meal.Lunch, "Wrap", 500, 30, 50, 15, default);
        var id = (await s.FoodLog.DayAsync(Day, default)).Entries.Single().Id;
        await s.FoodLog.DeleteAsync(id, default);
        await using var other = new UserScope(fixture, await fixture.CreateUserAsync());

        await other.Invoking(x => x.Undo.RestoreAsync(UndoKind.Entry, id, default)).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Purge_removes_only_rows_deleted_more_than_a_day_ago()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.FoodLog.AddQuickAsync(Day, Meal.Lunch, "Old", 100, 0, 0, 0, default);
        await s.FoodLog.AddQuickAsync(Day, Meal.Lunch, "Recent", 100, 0, 0, 0, default);
        var entries = (await s.FoodLog.DayAsync(Day, default)).Entries;
        var old = entries.Single(e => e.Description == "Old").Id;
        var recent = entries.Single(e => e.Description == "Recent").Id;
        await s.FoodLog.DeleteAsync(old, default);
        await s.FoodLog.DeleteAsync(recent, default);
        var now = DateTimeOffset.UtcNow;
        await s.Db.FoodLog.IgnoreQueryFilters([AppDbContext.SoftDeleteFilter]).Where(e => e.Id == old)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.DeletedAt, now.AddDays(-2)));

        await using (var system = fixture.CreateContext(null))
        {
            await SoftDeletePurge.PurgeAsync(system, now - SoftDeletePurge.Keep, default);
        }

        var left = await s.Db.FoodLog.IgnoreQueryFilters([AppDbContext.SoftDeleteFilter]).Where(e => e.Id == old || e.Id == recent).Select(e => e.Id).ToListAsync();
        left.Should().Equal(recent);
    }
}
```

Run: `dotnet test tests/GymTracker.Web.Tests --filter UndoTests`
Expected: build errors (no `UndoService`, no `DeletedAt`, no filter names).

- [ ] **Step 2: Domain and filters**

Add `public DateTimeOffset? DeletedAt { get; set; }` with the doc comment `/// <summary>Set when deleted; the row is hidden and can be restored for a while (undo), then purged.</summary>` to `FoodLogEntry`, `SetEntry`, `SessionExercise` and `TemplateExercise`.

In `AppDbContext` add:

```csharp
    /// <summary>Query filter names on the soft-deleted entities. Undo ignores only SoftDelete, never Owner.</summary>
    public const string OwnerFilter = "Owner";
    public const string SoftDeleteFilter = "SoftDelete";
```

Replace the filters on the four entities (an entity can't mix named and unnamed filters, so both become named):

```csharp
            // TemplateExercise
            e.HasQueryFilter(OwnerFilter, x => x.Template.UserId == CurrentUserId);
            e.HasQueryFilter(SoftDeleteFilter, x => x.DeletedAt == null);

            // SessionExercise
            e.HasQueryFilter(OwnerFilter, x => x.Session.UserId == CurrentUserId);
            e.HasQueryFilter(SoftDeleteFilter, x => x.DeletedAt == null);

            // SetEntry: also hidden while its exercise is removed, so history and volume skip it.
            e.HasQueryFilter(OwnerFilter, x => x.SessionExercise.Session.UserId == CurrentUserId);
            e.HasQueryFilter(SoftDeleteFilter, x => x.DeletedAt == null && x.SessionExercise.DeletedAt == null);

            // FoodLogEntry
            e.HasQueryFilter(OwnerFilter, x => x.UserId == CurrentUserId);
            e.HasQueryFilter(SoftDeleteFilter, x => x.DeletedAt == null);
```

Run: `dotnet ef migrations add AddSoftDelete --project src/GymTracker.Web --output-dir Data/Migrations` (four nullable `DeletedAt` columns; no data change).

Search for other `IgnoreQueryFilters()` calls (`grep -rn IgnoreQueryFilters src --include='*.cs'`). `SessionService.FindByClientIdAsync` ignores all filters and re-checks the owner; that stays correct.

- [ ] **Step 3: Deletes become soft**

- `FoodLogService.DeleteAsync`: replace `db.FoodLog.Remove(entry);` with `entry.DeletedAt = time.GetUtcNow();`
- `SessionService.DeleteSetAsync`: replace `db.Sets.Remove(set);` with `set.DeletedAt = time.GetUtcNow();`
- `SessionService.RemoveExerciseAsync`: replace `db.SessionExercises.Remove(item);` with `item.DeletedAt = time.GetUtcNow();` and update its doc comment to "Removes an exercise and hides its sets; undo can restore both."
- `TemplateService`: add `TimeProvider time` to the primary constructor; in `RemoveExerciseAsync` replace `db.TemplateExercises.Remove(item);` with `item.DeletedAt = time.GetUtcNow();`

- [ ] **Step 4: `UndoService` and the purge**

Create `src/GymTracker.Web/Features/Undo/UndoService.cs`:

```csharp
using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Undo;

public enum UndoKind
{
    Entry,
    Set,
    SessionExercise,
    TemplateExercise,
}

/// <summary>Brings back a soft-deleted row of the current user. The owner filter stays on, so other users' rows are not found.</summary>
public class UndoService(AppDbContext db, TimeProvider time)
{
    /// <summary>How long after a delete undo is still accepted (the toast shows for a few seconds; this allows for slow taps and retries).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private static readonly string[] SoftDelete = [AppDbContext.SoftDeleteFilter];

    public async Task RestoreAsync(UndoKind kind, int id, CancellationToken ct)
    {
        switch (kind)
        {
            case UndoKind.Entry:
                var entry = await db.FoodLog.IgnoreQueryFilters(SoftDelete).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
                if (Restore(entry.DeletedAt))
                {
                    entry.DeletedAt = null;
                }

                break;

            case UndoKind.Set:
                var set = await db.Sets.IgnoreQueryFilters(SoftDelete).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
                if (Restore(set.DeletedAt))
                {
                    set.DeletedAt = null;
                    var siblings = await db.Sets.Where(x => x.SessionExerciseId == set.SessionExerciseId && x.IsWarmup == set.IsWarmup && x.Id != set.Id).ToListAsync(ct);
                    var ordered = siblings.Append(set).OrderBy(x => x.SetNumber).ThenBy(x => x.CompletedAt).ThenBy(x => x.Id).ToList();
                    for (var i = 0; i < ordered.Count; i++)
                    {
                        ordered[i].SetNumber = i + 1;
                    }
                }

                break;

            case UndoKind.SessionExercise:
                var item = await db.SessionExercises.IgnoreQueryFilters(SoftDelete).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
                if (Restore(item.DeletedAt))
                {
                    item.DeletedAt = null;
                    var others = await db.SessionExercises.Where(x => x.SessionId == item.SessionId && x.Id != item.Id).ToListAsync(ct);
                    Renumber(others.Append(item), x => x.SortOrder, x => x.Id, (x, i) => x.SortOrder = i);
                }

                break;

            case UndoKind.TemplateExercise:
                var te = await db.TemplateExercises.IgnoreQueryFilters(SoftDelete).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
                if (Restore(te.DeletedAt))
                {
                    te.DeletedAt = null;
                    var others = await db.TemplateExercises.Where(x => x.TemplateId == te.TemplateId && x.Id != te.Id).ToListAsync(ct);
                    Renumber(others.Append(te), x => x.SortOrder, x => x.Id, (x, i) => x.SortOrder = i);
                }

                break;

            default:
                throw new NotFoundException();
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>True when the row is deleted and still inside the window; false when it's already live (a repeated undo).</summary>
    private bool Restore(DateTimeOffset? deletedAt)
    {
        if (deletedAt is null)
        {
            return false;
        }

        if (time.GetUtcNow() - deletedAt.Value > Window)
        {
            throw new ValidationException("It's too late to undo that.");
        }

        return true;
    }

    private static void Renumber<T>(IEnumerable<T> items, Func<T, int> order, Func<T, int> id, Action<T, int> set)
    {
        var ordered = items.OrderBy(order).ThenBy(id).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            set(ordered[i], i);
        }
    }
}
```

Note: the `SessionExercise` case uses `IgnoreQueryFilters(SoftDelete)`, which also drops `SetEntry`'s soft filter only for `Sets` queries in that statement; `Sets` loaded later in other statements keep it. Check the generated SQL for the `Set` case once (`db.Sets.IgnoreQueryFilters(SoftDelete)` must still join `Session.UserId`); the owner test covers it.

Create `src/GymTracker.Web/Features/Undo/SoftDeletePurge.cs`:

```csharp
using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Undo;

/// <summary>Hourly, permanently deletes rows that were soft-deleted more than a day ago.</summary>
public sealed class SoftDeletePurge(IServiceScopeFactory scopes, TimeProvider time, ILogger<SoftDeletePurge> log) : BackgroundService
{
    public static readonly TimeSpan Keep = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var purged = await PurgeAsync(db, time.GetUtcNow() - Keep, stoppingToken);
                if (purged > 0)
                {
                    log.LogInformation("Purged {Count} soft-deleted rows.", purged);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Soft-delete purge failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Runs without a user, so it must bypass every filter; it only touches rows deleted before the cutoff.</summary>
    public static async Task<int> PurgeAsync(AppDbContext db, DateTimeOffset cutoff, CancellationToken ct)
    {
        var count = await db.Sets.IgnoreQueryFilters().Where(x => x.DeletedAt < cutoff).ExecuteDeleteAsync(ct);
        count += await db.SessionExercises.IgnoreQueryFilters().Where(x => x.DeletedAt < cutoff).ExecuteDeleteAsync(ct);
        count += await db.TemplateExercises.IgnoreQueryFilters().Where(x => x.DeletedAt < cutoff).ExecuteDeleteAsync(ct);
        count += await db.FoodLog.IgnoreQueryFilters().Where(x => x.DeletedAt < cutoff).ExecuteDeleteAsync(ct);
        return count;
    }
}
```

In `Program.cs`:

```csharp
builder.Services.AddScoped<GymTracker.Web.Features.Undo.UndoService>();
builder.Services.AddHostedService<GymTracker.Web.Features.Undo.SoftDeletePurge>();
```

Run: `dotnet test tests/GymTracker.Web.Tests --filter UndoTests`
Expected: 8 passed. Then run the whole suite (`dotnet test`): existing tests that counted rows after a delete must still pass, because the filters hide soft-deleted rows. If a test reads with `IgnoreQueryFilters()`, update it to `IgnoreQueryFilters([AppDbContext.OwnerFilter])` only where it expects deleted rows to be gone.

- [ ] **Step 5: Translations, SPEC, commit**

Add to `strings.bg.json`: `"It's too late to undo that.": "Вече е късно това да се отмени.",`

SPEC.md section 4: "Food entries, sets, session exercises and template exercises are soft-deleted (`DeletedAt`), restorable for 10 minutes and purged after 24 hours."

Run `dotnet build` and `dotnet test`.

```bash
git add src tests SPEC.md
git commit -m "Soft-delete entries, sets and exercises so deletes can be undone"
```

---

### Task 8: Undo in the interface

**Files:**
- Create: `src/GymTracker.Web/Controllers/UndoController.cs`
- Modify: `src/GymTracker.Web/Features/Localization/Notices.cs` (`Undo` TempData)
- Modify: `src/GymTracker.Web/Views/Shared/_Layout.cshtml` (undo data in `#main`)
- Modify: `src/GymTracker.Web/Controllers/FoodController.cs`, `TrainController.cs`, `TemplatesController.cs` (set undo after delete)
- Modify: `src/GymTracker.Web/wwwroot/js/motion.js` (toast action), `src/GymTracker.Web/Features/Localization/ScriptText.cs`
- Modify: `src/GymTracker.Web/wwwroot/css/app.css`
- Modify: `src/GymTracker.Web/Views/Food/Entry.cshtml`, `Views/Shared/_ExerciseCard.cshtml`, `Views/Templates/Edit.cshtml` (drop `data-confirm`)
- Test: `tests/GymTracker.Web.Tests/UndoEndpointTests.cs`

**Interfaces:**
- Consumes: `UndoService.RestoreAsync`, `UndoKind`.
- Produces: `POST /undo/{kind}/{id}` with `kind` in `entry`, `set`, `session-exercise`, `template-exercise` and form field `returnUrl`; redirects to the local return URL.
- Produces: `Notices.Undo(this ITempDataDictionary temp, LocalText text, string url)`, `Notices.ReadUndo(ITempDataDictionary temp) : (LocalText Text, string Url)?`.
- Produces: `window.gymToast(text, action)` where `action` is `{ label, url }` or omitted; the `gym:undo` htmx event with detail `{ text, url }`.

- [ ] **Step 1: Failing endpoint tests**

Create `tests/GymTracker.Web.Tests/UndoEndpointTests.cs`:

```csharp
using System.Net;
using FluentAssertions;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class UndoEndpointTests(PostgresFixture fixture)
{
    private static FormUrlEncodedContent Form(string token, params (string, string)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)).Append(new("__RequestVerificationToken", token)));

    [Fact]
    public async Task Deleting_an_entry_offers_undo_and_undo_brings_it_back()
    {
        await using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/food");
        await client.PostAsync("/food/quick", Form(token, ("date", "2026-10-04"), ("meal", "Lunch"), ("description", "Wrap"), ("kcal", "500")));
        var day = await client.GetStringAsync("/food?date=2026-10-04");
        var id = System.Text.RegularExpressions.Regex.Match(day, "/food/entries/(\\d+)").Groups[1].Value;

        var deleted = await client.PostAsync($"/food/entries/{id}/delete", Form(token));
        deleted.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var after = await client.GetStringAsync(deleted.Headers.Location!.ToString());
        after.Should().Contain($"data-undo-url=\"/undo/entry/{id}\"").And.NotContain("Wrap</span>");

        var undone = await client.PostAsync($"/undo/entry/{id}", Form(token, ("returnUrl", "/food?date=2026-10-04")));
        undone.Headers.Location!.ToString().Should().Be("/food?date=2026-10-04");
        (await client.GetStringAsync("/food?date=2026-10-04")).Should().Contain("Wrap");
    }

    [Fact]
    public async Task Unknown_kinds_are_not_found()
    {
        await using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/food");

        (await client.PostAsync("/undo/session/1", Form(token, ("returnUrl", "/food")))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_outside_return_url_falls_back_to_today()
    {
        await using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/food");
        await client.PostAsync("/food/quick", Form(token, ("date", "2026-10-04"), ("meal", "Lunch"), ("kcal", "500")));
        var id = System.Text.RegularExpressions.Regex.Match(await client.GetStringAsync("/food?date=2026-10-04"), "/food/entries/(\\d+)").Groups[1].Value;
        await client.PostAsync($"/food/entries/{id}/delete", Form(token));

        var undone = await client.PostAsync($"/undo/entry/{id}", Form(token, ("returnUrl", "https://evil.example/")));

        undone.Headers.Location!.ToString().Should().Be("/today");
    }
}
```

Run: `dotnet test tests/GymTracker.Web.Tests --filter UndoEndpointTests`
Expected: FAIL (no `/undo` route, no `data-undo-url`).

- [ ] **Step 2: Controller and notices**

Create `src/GymTracker.Web/Controllers/UndoController.cs`:

```csharp
using GymTracker.Web.Features;
using GymTracker.Web.Features.Undo;
using Microsoft.AspNetCore.Mvc;

namespace GymTracker.Web.Controllers;

[Route("undo")]
public class UndoController(UndoService undo) : Controller
{
    private static readonly Dictionary<string, UndoKind> Kinds = new()
    {
        ["entry"] = UndoKind.Entry,
        ["set"] = UndoKind.Set,
        ["session-exercise"] = UndoKind.SessionExercise,
        ["template-exercise"] = UndoKind.TemplateExercise,
    };

    [HttpPost("{kind}/{id:int}")]
    public async Task<IActionResult> Restore(string kind, int id, string? returnUrl, CancellationToken ct)
    {
        if (!Kinds.TryGetValue(kind, out var k))
        {
            return NotFound();
        }

        await undo.RestoreAsync(k, id, ct);
        return Redirect(Url.SafeReturnUrl(returnUrl));
    }
}
```

In `Notices.cs` add:

```csharp
    public const string UndoKey = "Undo";

    /// <summary>Shows "{text}" with an Undo button on the next page; url is the POST that restores the row.</summary>
    public static void Undo(this ITempDataDictionary temp, LocalText text, string url) =>
        temp[UndoKey] = Pack(text) + Separator + Separator + url;

    public static (LocalText Text, string Url)? ReadUndo(ITempDataDictionary temp)
    {
        if (temp[UndoKey] is not string s || s.LastIndexOf($"{Separator}{Separator}", StringComparison.Ordinal) is var at && at < 0)
        {
            return null;
        }

        return (Read(s[..at])!, s[(at + 2)..]);
    }
```

In `_Layout.cshtml`, next to `var message = ...` add `var undo = Notices.ReadUndo(TempData);`, and inside `<main>` before `@RenderBody()`:

```cshtml
            @if (undo is { } u)
            {
                <div hidden data-undo-url="@u.Url" data-undo-text="@L[u.Text]"></div>
            }
```

- [ ] **Step 3: Deletes set the undo notice**

- `FoodController.DeleteEntry`: before the redirect, `TempData.Undo("Entry deleted.", $"/undo/entry/{id}");`
- `TrainController.RemoveExercise`:

```csharp
    [HttpPost("session-exercises/{id:int}/remove")]
    public async Task<IActionResult> RemoveExercise(int id, CancellationToken ct)
    {
        var sessionId = await sessions.RemoveExerciseAsync(id, ct);
        TempData.Undo("Exercise removed.", $"/undo/session-exercise/{id}");
        return Redirect($"/train/sessions/{sessionId}");
    }
```

- `TemplatesController.RemoveExercise`: before the redirect, `TempData.Undo("Exercise removed.", $"/undo/template-exercise/{id}");`
- `TrainController.DeleteSet`: before returning the partial,

```csharp
        // htmx turns this header into a "gym:undo" event; motion.js shows the toast. The text is a script string (t()).
        Response.Headers["HX-Trigger"] = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["gym:undo"] = new { text = "Set deleted.", url = $"/undo/set/{id}" },
        });
```

(`TempData.Undo` needs `using GymTracker.Web.Features.Localization;` and the `LocalText` implicit conversion from string; check `LocalText` has `implicit operator LocalText(string)`, as `return "..."` in `FoodMath.Validate` already relies on it. The `Undo(` call is not matched by `CatalogueTests`' patterns, so also add the two texts to `CatalogueTests.Extra`: `"Entry deleted.", "Exercise removed."`.)

Drop `data-confirm` from: the delete form in `Food/Entry.cshtml`, the Remove form in `_ExerciseCard.cshtml`, and the Remove form in `Templates/Edit.cshtml`.

- [ ] **Step 4: Toast with an action**

In `motion.js` replace `window.gymToast` with:

```js
    let toastTimer;
    // An action ({ label, url }) adds a button that POSTs to url and comes back to this page (nav.js swaps it in).
    window.gymToast = function (text, action) {
        const el = document.getElementById("toast");
        if (!el) {
            return;
        }
        const message = document.createElement("span");
        message.textContent = text;
        el.replaceChildren(message);
        if (action) {
            const form = document.createElement("form");
            form.method = "post";
            form.action = action.url;
            form.className = "toast-action";
            for (const [name, value] of [["__RequestVerificationToken", window.gymToken ? window.gymToken() : ""], ["returnUrl", location.pathname + location.search]]) {
                const input = document.createElement("input");
                input.type = "hidden";
                input.name = name;
                input.value = value;
                form.appendChild(input);
            }
            const button = document.createElement("button");
            button.type = "submit";
            button.textContent = action.label;
            form.appendChild(button);
            form.addEventListener("submit", () => { el.hidden = true; });
            el.appendChild(form);
        }
        el.hidden = false;
        clearTimeout(toastTimer);
        toastTimer = setTimeout(() => {
            if (reduce.matches) {
                el.hidden = true;
                return;
            }
            el.animate([{ opacity: 1 }, { opacity: 0, transform: "translateY(12px)" }], { duration: 220, easing: "ease-in" })
                .finished.then(() => { el.hidden = true; });
        }, action ? 6000 : 5000);
    };

    // Undo offered by the server: after a redirect (data in #main) or by an htmx response (gym:undo event).
    function offerUndo(scope) {
        const data = scope.querySelector("[data-undo-url]");
        if (data) {
            window.gymToast(data.dataset.undoText, { label: t("Undo"), url: data.dataset.undoUrl });
        }
    }
    offerUndo(document);
    document.addEventListener("gym:page", () => offerUndo(document.getElementById("main")));
    document.body.addEventListener("gym:undo", (event) => {
        window.gymToast(t(event.detail.text), { label: t("Undo"), url: event.detail.url });
    });
```

(`gymToken` is defined in `site.js`, which loads after `motion.js`; it's read at click time, so the order doesn't matter. Check that `t` is available in `motion.js` the same way other scripts use it.)

In `ScriptText.Keys` add a `// motion.js` group with `"Undo"` and `"Set deleted."`.

In `app.css`, after the `.toast` rule:

```css
.toast:has(.toast-action) { display: flex; align-items: center; gap: var(--s-3); padding-right: var(--s-2); }
.toast-action { margin-left: auto; }
.toast-action button { min-height: var(--tap); padding: 0 var(--s-3); color: var(--accent-text); font-weight: var(--weight-semibold); }
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/GymTracker.Web.Tests --filter "UndoEndpointTests|CatalogueTests"`
Expected: pass after translations (next step).

- [ ] **Step 6: Translations, SPEC, browser check, commit**

Add to `strings.bg.json`:

```json
  "Entry deleted.": "Записът е изтрит.",
  "Exercise removed.": "Упражнението е премахнато.",
  "Set deleted.": "Серията е изтрита.",
  "Undo": "Отмени",
```

SPEC.md section 7: "Deleting a food entry or a set, and removing an exercise from a session or template, happen at once with an Undo toast for 6 seconds; archive, finish and delete workout still ask first."

Browser check at 390 px in both languages: delete a food entry (no confirm, toast with Undo, Undo brings it back on the same page); swipe-delete a set and undo it (row returns after the page reloads); remove a session exercise and a template exercise and undo each; the Undo button is at least 44 px tall and the toast doesn't cover the tab bar.

Run `dotnet build` and `dotnet test`.

```bash
git add src tests SPEC.md
git commit -m "Replace delete confirms with an Undo toast"
```

---

## After all tasks

- Run the whole suite once more and the browser checks for the pages touched by more than one task (session card: notes, plates, swap, undo; Food day: quick add, save meal, undo).
- Deploy only when Antoan asks; three migrations (`AddExerciseSettings`, `AddSavedMeals`, `AddSoftDelete`) apply on start. Take a `pg_dump` on wizard first as in earlier deploys.
