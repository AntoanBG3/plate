# Phase 13: Label photos and shared foods — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fill the manual food form from a photo of a nutrition label using a separately chosen vision model, let barcode foods be shared or kept private with an anonymous "Added by another user" mark, and centre the food composer's text.

**Architecture:** A pure `LabelValues.Normalize` in Core turns what the model read into per-100 g form values. `OpenRouterLabelReader` sends the photo as an image content part with strict structured output, behind `ILabelReader` and a switching wrapper like the other AI tasks. `LabelScanService` stores every read and its photo, and the manual form gets the photo button, a share checkbox and an optional barcode field. `FoodItem.AddedByUserId` plus a split barcode index make private barcode foods possible and the attribution computable.

**Tech Stack:** .NET 10, ASP.NET Core MVC + Razor + htmx 2.0, EF Core + Npgsql (PostgreSQL 18), OpenRouter chat completions, xUnit + FluentAssertions + Testcontainers, Playwright (E2E project).

**Spec:** `SPEC.md` sections 4 (FoodItem, LabelScan), 6.6, 7 (Food), 9, 11, 12 and Phase 13. Committed in dd12059 on branch `label-photos`.

## Global Constraints

- `Plate.Core` has no references to EF Core, ASP.NET Core or HttpClient. `LabelValues` is pure.
- Every 6.6 normalizing case gets a Core unit test before any UI uses it.
- Every user-owned query is scoped to the current user; a scan id or food id from the client is checked for ownership (missing or foreign scan id never blocks saving food).
- No new NuGet packages or front-end libraries. The barcode detector already vendored in `wwwroot/lib/barcode-detector` is reused.
- Weights in grams, energy in kcal. Numbers render with a dot in both languages (SPEC 8).
- Mobile-first at 390 px; touch targets at least 44 px. No CDNs; any new JS file is added to `wwwroot/sw.js` precache.
- No secrets in the repo. The label model default is `google/gemini-3.5-flash-lite`, setting name `OpenRouter:Model:Label`.
- Limits: 20 label reads per user per 10 minutes (`RateLimits:AiLabelPerWindow`), `POST /food/label` body up to 3 MB, stored photo up to 2 MB, phone resize to 2,000 px long side at JPEG 0.85.
- Retention: label photos 30 days, LabelScans 180 days, both in the hourly `SoftDeletePurge`.
- Attribution text is exactly "Added by another user" ("Добавено от друг потребител"); never a name, email or id.
- Model call: 20 s timeout, one retry on 5xx only, no retry on timeout.
- Every user-facing string is in `Resources/strings.bg.json`; `CatalogueTests` and `BulgarianCatalogueTests` stay green. Regenerate keys with `GYM_WRITE_KEYS=1 dotnet test tests/Plate.Web.Tests --filter CatalogueTests`.
- Test cadence (memory): after each task run the affected tests plus all Core and Web tests; run the full E2E suite at the end of Task 10 and before merging.
- Each task is one commit ending with the attribution lines from the session.

## Review Focus

1. **A photo the browser can't decode** (HEIC from the library on desktop Chrome, a corrupt file): the user expects a short message and an untouched form, not a stuck "Reading…" indicator. Pinned in Task 9 (E2E `UndecodablePhotoLeavesFormAlone`).
2. **A second photo picked while the first is still being read**: only the latest result should fill the form; the earlier request is aborted. Pinned in Task 9 (E2E `SecondPhotoWins`).
3. **Values already typed before scanning**: the name the user typed stays, and the numbers are replaced by the label's. Pinned in Task 9 (E2E `TypedNameIsKept`).
4. **Two people saving the same barcode at once, or a stale form after someone else shared it**: a friendly validation message, never a 500 from the unique index. Pinned in Task 4 (`SharingRaceGivesValidationError`).
5. **Bulgarian account**: filled values must render with a dot (`12.5`, not `12,5`) or the inputs' `pattern` rejects them on iOS. Pinned in Task 8 (`LabelFormUsesDotDecimalsInBulgarian`).

---

### Task 1: Centre the composer text

**Files:**
- Modify: `src/Plate.Web/wwwroot/css/app.css:400`
- Test: `tests/Plate.E2E.Tests/LayoutTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: nothing other tasks use.

- [ ] **Step 1: Write the failing E2E test.** Add to `LayoutTests.cs`, following the existing fixture use (`E2EFixture`, `Phone` at 390 px). It runs in Chromium and WebKit like the other layout tests, for English and Bulgarian, on `/food/add?tab=text` and on the setup chat (`/welcome`, with the AI app so the composer shows).

```csharp
[Theory]
[MemberData(nameof(BrowsersAndLanguages))]
public async Task ComposerTextIsLevelWithSendButton(string browser, string language)
{
    var page = await fixture.SignedInPageAsync(browser, Phone.Width390, language);
    foreach (var (path, ai) in new[] { ("/food/add?tab=text", false), ("/welcome/chat", true) })
    {
        await page.GotoAsync(fixture.Url(path, ai));
        var offset = await page.EvaluateAsync<double>("""
            () => {
              const ta = document.querySelector('.composer textarea');
              const btn = document.querySelector('.composer .send-btn');
              const s = getComputedStyle(ta);
              const r = ta.getBoundingClientRect();
              const lineMid = r.top + parseFloat(s.paddingTop) + parseFloat(s.lineHeight) / 2;
              const b = btn.getBoundingClientRect();
              return Math.abs(lineMid - (b.top + b.height / 2));
            }
            """);
        offset.Should().BeLessThanOrEqualTo(1, $"{path} in {language}");
    }
}
```

Use whatever helper names `LayoutTests.cs` already has for signing in, choosing the AI app and the browser matrix; adjust the two calls above to match. If the setup chat composer lives at another path, take it from `Views/Onboarding`.

- [ ] **Step 2: Run it and see it fail.** `dotnet test tests/Plate.E2E.Tests --filter ComposerTextIsLevelWithSendButton`. Expected: FAIL with an offset of about 3.5.

- [ ] **Step 3: Fix the padding.** In `app.css:400` change `padding: 7px 0;` to:

```css
padding: calc((44px - 1.35em) / 2) 0;
```

The textarea is 44 px tall (with `margin: -4px 0` inside the 6 px composer padding), so this puts a single line's middle at the composer's middle, which is where the 36 px send button sits. `align-items: flex-end` stays, so the button stays at the bottom as the text grows.

- [ ] **Step 4: Run it and see it pass.** Same command. Expected: PASS for all browser and language cases. Also run `VisualSystemTests` (the 51×31 regression) to make sure nothing else moved.

- [ ] **Step 5: Commit.** "Centre the composer's first line on the send button"

---

### Task 2: Core label types and normalizing

**Files:**
- Create: `src/Plate.Core/Nutrition/LabelReading.cs`
- Test: `tests/Plate.Core.Tests/LabelValuesTests.cs`

**Interfaces:**
- Consumes: `Per100g`, `FoodMath.MacrosDisagree` (`src/Plate.Core/Nutrition/FoodMath.cs`), `LocalText`.
- Produces (used by Tasks 6 to 10):
  - `enum LabelBasis { Per100g, Per100ml, PerServing }`
  - `record LabelReading(bool IsLabel, string? Name, string? Brand, LabelBasis Basis, decimal? ServingGrams, decimal? EnergyKcal, decimal? EnergyKj, decimal? Protein, decimal? Carbs, decimal? Fat, decimal? SaturatedFat, decimal? Fiber)`
  - `record LabelReadResult(LabelReading? Reading, string? Raw, LocalText? Error, string? Model)` with `static Failed(LocalText error, string? model = null, string? raw = null)`
  - `interface ILabelReader { Task<bool> IsAvailableAsync(CancellationToken ct); Task<LabelReadResult> ReadAsync(byte[] image, string contentType, CancellationToken ct); }`
  - `[Flags] enum LabelField { None = 0, Kcal = 1, Protein = 2, Carbs = 4, Fat = 8, SaturatedFat = 16, Fiber = 32 }`
  - `record LabelValues(string? Name, string? Brand, decimal? Kcal, decimal? Protein, decimal? Carbs, decimal? Fat, decimal? SaturatedFat, decimal? Fiber, decimal? ServingGrams, LabelField Converted, IReadOnlyList<LocalText> Notes, IReadOnlyList<LocalText> Warnings)` with `static LabelOutcome Normalize(LabelReading r)` and `static bool Edited(LabelValues suggested, Per100g saved, decimal? savedServing)`
  - `record LabelOutcome(LabelValues? Values, LocalText? Error)`
  - Message constants on `LabelValues`: `NotFound`, `PerServingOnly`.

- [ ] **Step 1: Write the failing tests.** One test per 6.6 case.

```csharp
using FluentAssertions;
using Plate.Core.Nutrition;

namespace Plate.Core.Tests;

public class LabelValuesTests
{
    private static LabelReading Label(
        LabelBasis basis = LabelBasis.Per100g, decimal? serving = null, decimal? kcal = 250, decimal? kj = null,
        decimal? protein = 10, decimal? carbs = 30, decimal? fat = 10, decimal? sat = 4, decimal? fiber = 2, bool isLabel = true) =>
        new(isLabel, "Кисело мляко", "Danone", basis, serving, kcal, kj, protein, carbs, fat, sat, fiber);

    [Fact]
    public void NotALabelIsNotFound() =>
        LabelValues.Normalize(Label(isLabel: false)).Error.Should().Be(LabelValues.NotFound);

    [Fact]
    public void NothingReadIsNotFound() =>
        LabelValues.Normalize(Label(kcal: null, protein: null, carbs: null, fat: null)).Error.Should().Be(LabelValues.NotFound);

    [Fact]
    public void KilojoulesOnlyBecomeKcal()
    {
        var v = LabelValues.Normalize(Label(kcal: null, kj: 1046)).Values!;
        v.Kcal.Should().Be(250);
        v.Converted.Should().HaveFlag(LabelField.Kcal);
    }

    [Fact]
    public void AgreeingEnergyHasNoNote() =>
        LabelValues.Normalize(Label(kcal: 250, kj: 1046)).Values!.Notes.Should().BeEmpty();

    [Fact]
    public void DisagreeingEnergyKeepsKcalWithNote()
    {
        var v = LabelValues.Normalize(Label(kcal: 250, kj: 1300)).Values!;
        v.Kcal.Should().Be(250);
        v.Notes.Should().Contain(LabelValues.EnergyDisagrees);
    }

    [Fact]
    public void PerServingScalesTo100g()
    {
        var v = LabelValues.Normalize(Label(LabelBasis.PerServing, serving: 30, kcal: 120, protein: 3, carbs: 18, fat: 4, sat: 1.5m, fiber: null)).Values!;
        v.Kcal.Should().Be(400);
        v.Protein.Should().Be(10);
        v.Carbs.Should().Be(60);
        v.Fat.Should().Be(13.3m);
        v.SaturatedFat.Should().Be(5);
        v.Fiber.Should().BeNull();
        v.ServingGrams.Should().Be(30);
        v.Converted.Should().Be(LabelField.Kcal | LabelField.Protein | LabelField.Carbs | LabelField.Fat | LabelField.SaturatedFat);
    }

    [Fact]
    public void PerServingWithoutWeightFails() =>
        LabelValues.Normalize(Label(LabelBasis.PerServing, serving: null)).Error.Should().Be(LabelValues.PerServingOnly);

    [Fact]
    public void Per100mlIsUsedAsPer100gWithNote() =>
        LabelValues.Normalize(Label(LabelBasis.Per100ml)).Values!.Notes.Should().Contain(LabelValues.MillilitresAsGrams);

    [Fact]
    public void RoundsToOneDecimalAndKcalToWhole()
    {
        var v = LabelValues.Normalize(Label(kcal: 250.6m, protein: 12.345m, carbs: 30.05m)).Values!;
        v.Kcal.Should().Be(251);
        v.Protein.Should().Be(12.3m);
        v.Carbs.Should().Be(30.1m);
    }

    [Fact]
    public void ImpossibleValuesAreLeftEmptyWithNote()
    {
        var v = LabelValues.Normalize(Label(kcal: 950, fat: -1)).Values!;
        v.Kcal.Should().BeNull();
        v.Fat.Should().BeNull();
        v.Notes.Should().Contain(LabelValues.LeftEmpty);
    }

    [Fact]
    public void MacrosOver100gWarnButStay()
    {
        var v = LabelValues.Normalize(Label(kcal: 500, protein: 40, carbs: 50, fat: 11)).Values!;
        v.Protein.Should().Be(40);
        v.Warnings.Should().Contain(LabelValues.MacrosOver100);
    }

    [Fact]
    public void SaturatedAboveFatWarns() =>
        LabelValues.Normalize(Label(fat: 3, sat: 5)).Values!.Warnings.Should().Contain(LabelValues.SaturatedAboveFat);

    [Fact]
    public void EnergyThatDoesNotFitMacrosWarns() =>
        LabelValues.Normalize(Label(kcal: 100, protein: 10, carbs: 30, fat: 10)).Values!.Warnings.Should().Contain(LabelValues.EnergyMismatch);

    [Fact]
    public void ConsistentLabelHasNoWarnings() =>
        LabelValues.Normalize(Label(kcal: 250, protein: 10, carbs: 30, fat: 10)).Values!.Warnings.Should().BeEmpty();

    [Theory]
    [InlineData(0)]
    [InlineData(5000)]
    public void ServingOutsideLimitsIsDropped(int grams) =>
        LabelValues.Normalize(Label(serving: grams)).Values!.ServingGrams.Should().BeNull();

    [Fact]
    public void NameAndBrandPassThrough()
    {
        var v = LabelValues.Normalize(Label()).Values!;
        v.Name.Should().Be("Кисело мляко");
        v.Brand.Should().Be("Danone");
    }

    [Theory]
    [InlineData(250, 10, 30, 10, null, false)]
    [InlineData(250, 10.04, 30, 10, null, false)]
    [InlineData(250.4, 10, 30, 10, null, false)]
    [InlineData(250, 10.2, 30, 10, null, true)]
    [InlineData(260, 10, 30, 10, null, true)]
    [InlineData(250, 10, 30, 10, 40, true)]
    public void EditedComparesBeyondRounding(double kcal, double protein, double carbs, double fat, double? serving, bool edited)
    {
        var suggested = LabelValues.Normalize(Label(kcal: 250, protein: 10, carbs: 30, fat: 10, sat: 4, fiber: 2)).Values!;
        var saved = new Per100g((decimal)kcal, (decimal)protein, (decimal)carbs, (decimal)fat, 2, 4);
        LabelValues.Edited(suggested, saved, (decimal?)serving).Should().Be(edited);
    }

    [Fact]
    public void FillingAnEmptyValueCountsAsEdited()
    {
        var suggested = LabelValues.Normalize(Label(fiber: null)).Values!;
        LabelValues.Edited(suggested, new Per100g(250, 10, 30, 10, 3, 4), null).Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Plate.Core.Tests --filter LabelValuesTests`. Expected: build error, `LabelValues` not defined.

- [ ] **Step 3: Implement `LabelReading.cs`.**

```csharp
using Plate.Core.Text;

namespace Plate.Core.Nutrition;

public enum LabelBasis
{
    Per100g,
    Per100ml,
    PerServing,
}

/// <summary>What the label model read from a photo, as printed in the column it chose (SPEC 6.6). Nothing is computed.</summary>
public record LabelReading(
    bool IsLabel,
    string? Name,
    string? Brand,
    LabelBasis Basis,
    decimal? ServingGrams,
    decimal? EnergyKcal,
    decimal? EnergyKj,
    decimal? Protein,
    decimal? Carbs,
    decimal? Fat,
    decimal? SaturatedFat,
    decimal? Fiber);

/// <param name="Raw">The model's answer as returned, stored on the scan.</param>
public record LabelReadResult(LabelReading? Reading, string? Raw, LocalText? Error, string? Model)
{
    public static LabelReadResult Failed(LocalText error, string? model = null, string? raw = null) => new(null, raw, error, model);
}

public interface ILabelReader
{
    Task<bool> IsAvailableAsync(CancellationToken ct);

    Task<LabelReadResult> ReadAsync(byte[] image, string contentType, CancellationToken ct);
}

[Flags]
public enum LabelField
{
    None = 0,
    Kcal = 1,
    Protein = 2,
    Carbs = 4,
    Fat = 8,
    SaturatedFat = 16,
    Fiber = 32,
}

public record LabelOutcome(LabelValues? Values, LocalText? Error);

/// <summary>Per-100 g values for the manual food form, made from a label reading (SPEC 6.6). Null means leave the field empty.</summary>
/// <param name="Converted">Fields computed from kJ or from a per-serving column, highlighted for checking.</param>
public record LabelValues(
    string? Name,
    string? Brand,
    decimal? Kcal,
    decimal? Protein,
    decimal? Carbs,
    decimal? Fat,
    decimal? SaturatedFat,
    decimal? Fiber,
    decimal? ServingGrams,
    LabelField Converted,
    IReadOnlyList<LocalText> Notes,
    IReadOnlyList<LocalText> Warnings)
{
    public static readonly LocalText NotFound = "Couldn't find a nutrition table in that photo.";
    public static readonly LocalText PerServingOnly = "The label only gives values per serving. Enter them by hand or photograph the per-100 g column.";
    public static readonly LocalText EnergyDisagrees = "The label's kJ and kcal disagree; the kcal value was used.";
    public static readonly LocalText MillilitresAsGrams = "Per 100 ml, used as per 100 g.";
    public static readonly LocalText LeftEmpty = "Some values were outside what a food can have and were left empty.";
    public static readonly LocalText MacrosOver100 = "Protein, carbs and fat add up to more than 100 g. Check them.";
    public static readonly LocalText SaturatedAboveFat = "Saturated fat is more than fat. Check both.";
    public static readonly LocalText EnergyMismatch = "The calories don't match the protein, carbs and fat. Check the energy row.";

    private const decimal KjPerKcal = 4.184m;

    public static LabelOutcome Normalize(LabelReading r)
    {
        if (!r.IsLabel || r is { EnergyKcal: null, EnergyKj: null, Protein: null, Carbs: null, Fat: null })
        {
            return new LabelOutcome(null, NotFound);
        }

        var notes = new List<LocalText>();
        var warnings = new List<LocalText>();
        var converted = LabelField.None;

        var kcal = r.EnergyKcal;
        if (kcal is null && r.EnergyKj is { } kjOnly)
        {
            kcal = kjOnly / KjPerKcal;
            converted |= LabelField.Kcal;
        }
        else if (kcal is > 0 && r.EnergyKj is { } kj && Math.Abs(kj / KjPerKcal - kcal.Value) > 0.05m * kcal.Value)
        {
            notes.Add(EnergyDisagrees);
        }

        var factor = 1m;
        if (r.Basis == LabelBasis.PerServing)
        {
            if (r.ServingGrams is not > 0)
            {
                return new LabelOutcome(null, PerServingOnly);
            }

            factor = 100m / r.ServingGrams.Value;
            converted |= Flag(kcal, LabelField.Kcal) | Flag(r.Protein, LabelField.Protein) | Flag(r.Carbs, LabelField.Carbs)
                | Flag(r.Fat, LabelField.Fat) | Flag(r.SaturatedFat, LabelField.SaturatedFat) | Flag(r.Fiber, LabelField.Fiber);
        }
        else if (r.Basis == LabelBasis.Per100ml)
        {
            notes.Add(MillilitresAsGrams);
        }

        var dropped = false;
        decimal? Keep(decimal? value, decimal max, int decimals)
        {
            if (value is not { } v)
            {
                return null;
            }

            var scaled = Math.Round(v * factor, decimals, MidpointRounding.AwayFromZero);
            if (scaled < 0 || scaled > max)
            {
                dropped = true;
                return null;
            }

            return scaled;
        }

        var outKcal = Keep(kcal, 900, 0);
        var protein = Keep(r.Protein, 100, 1);
        var carbs = Keep(r.Carbs, 100, 1);
        var fat = Keep(r.Fat, 100, 1);
        var saturated = Keep(r.SaturatedFat, 100, 1);
        var fiber = Keep(r.Fiber, 100, 1);
        if (dropped)
        {
            notes.Add(LeftEmpty);
        }

        if ((protein ?? 0) + (carbs ?? 0) + (fat ?? 0) > 100)
        {
            warnings.Add(MacrosOver100);
        }

        if (saturated > fat)
        {
            warnings.Add(SaturatedAboveFat);
        }

        if (outKcal is { } k && protein is { } p && carbs is { } c && fat is { } f && FoodMath.MacrosDisagree(new Per100g(k, p, c, f)))
        {
            warnings.Add(EnergyMismatch);
        }

        var serving = r.ServingGrams is > 0 and <= FoodMath.MaxGrams ? Math.Round(r.ServingGrams.Value, 1, MidpointRounding.AwayFromZero) : (decimal?)null;
        return new LabelOutcome(
            new LabelValues(Clean(r.Name), Clean(r.Brand), outKcal, protein, carbs, fat, saturated, fiber, serving, converted, notes, warnings),
            null);
    }

    /// <summary>Whether what was saved differs from what was suggested beyond rounding (0.5 kcal, 0.05 g).</summary>
    public static bool Edited(LabelValues suggested, Per100g saved, decimal? savedServing) =>
        Differs(suggested.Kcal, saved.Kcal, 0.5m)
        || Differs(suggested.Protein, saved.Protein, 0.05m)
        || Differs(suggested.Carbs, saved.Carbs, 0.05m)
        || Differs(suggested.Fat, saved.Fat, 0.05m)
        || Differs(suggested.SaturatedFat, saved.SaturatedFat, 0.05m)
        || Differs(suggested.Fiber, saved.Fiber, 0.05m)
        || Differs(suggested.ServingGrams, savedServing, 0.05m);

    private static bool Differs(decimal? a, decimal? b, decimal tolerance) =>
        a is null != b is null || a is { } x && b is { } y && Math.Abs(x - y) > tolerance;

    private static LabelField Flag(decimal? value, LabelField field) => value is null ? LabelField.None : field;

    private static string? Clean(string? s)
    {
        var t = s?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > 200 ? t[..200] : t;
    }
}
```

- [ ] **Step 4: Run and see them pass.** Same command. Expected: all pass. Then run all Core tests.

- [ ] **Step 5: Commit.** "Core: label reading types and LabelValues.Normalize"

---

### Task 3: Data model and migration

**Files:**
- Create: `src/Plate.Core/Domain/LabelScan.cs`
- Modify: `src/Plate.Core/Domain/Nutrition.cs:35-56` (FoodItem), `src/Plate.Web/Data/AppDbContext.cs` (DbSets near line 32, FoodItem config near line 151-162, new entity configs after ParseRun near line 255)
- Create: migration `AddLabelScansAndSharedBy` via `dotnet ef`
- Test: `tests/Plate.Web.Tests/MigrationTests.cs` (stays green), new `tests/Plate.Web.Tests/LabelDataTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `FoodItem.AddedByUserId` (`string?`); `LabelScan` and `LabelScanPhoto` entities; `AppDbContext.LabelScans`, `AppDbContext.LabelScanPhotos`.

- [ ] **Step 1: Add the entities.** In `Nutrition.cs`, inside `FoodItem` after `OwnerUserId`:

```csharp
    /// <summary>Who created a manual item; only used to say a shared food came from someone else (SPEC 4).</summary>
    public string? AddedByUserId { get; set; }
```

New `LabelScan.cs`:

```csharp
namespace Plate.Core.Domain;

/// <summary>One label photo read by the label model (SPEC 6.6), kept to measure misreads.</summary>
public class LabelScan
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Model { get; set; } = "";
    public int LatencyMs { get; set; }
    public string? Error { get; set; }

    /// <summary>JSON: the model's answer as returned.</summary>
    public string? Reading { get; set; }

    /// <summary>JSON: the normalized LabelValues offered in the form.</summary>
    public string? Values { get; set; }

    public int? FoodItemId { get; set; }

    /// <summary>JSON: what the user saved; null until saved.</summary>
    public string? Saved { get; set; }

    public bool Edited { get; set; }
    public LabelScanPhoto? Photo { get; set; }
}

/// <summary>The scan's photo, in its own table so lists never load it. Deleted after 30 days.</summary>
public class LabelScanPhoto
{
    public const int MaxBytes = 2 * 1024 * 1024;

    public int Id { get; set; }
    public int LabelScanId { get; set; }
    public LabelScan LabelScan { get; set; } = null!;
    public string ContentType { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[] Bytes { get; set; } = [];
}
```

- [ ] **Step 2: Configure them.** In `AppDbContext.cs`:

```csharp
    public DbSet<LabelScan> LabelScans => Set<LabelScan>();
    public DbSet<LabelScanPhoto> LabelScanPhotos => Set<LabelScanPhoto>();
```

In the FoodItem block, replace `e.HasIndex(x => x.Barcode).IsUnique().HasFilter("\"Barcode\" IS NOT NULL");` with:

```csharp
            // A barcode is unique among shared foods, and per owner among private ones (SPEC 4).
            e.HasIndex(x => x.Barcode).IsUnique().HasFilter("\"Barcode\" IS NOT NULL AND \"OwnerUserId\" IS NULL").HasDatabaseName("IX_FoodItems_Barcode_Shared");
            e.HasIndex(x => new { x.OwnerUserId, x.Barcode }).IsUnique().HasFilter("\"Barcode\" IS NOT NULL AND \"OwnerUserId\" IS NOT NULL").HasDatabaseName("IX_FoodItems_Owner_Barcode");
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.AddedByUserId).OnDelete(DeleteBehavior.SetNull);
```

After the ParseRun block:

```csharp
        builder.Entity<LabelScan>(e =>
        {
            e.HasQueryFilter(x => x.UserId == CurrentUserId);
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.Error).HasMaxLength(300);
            e.Property(x => x.Reading).HasColumnType("jsonb");
            e.Property(x => x.Values).HasColumnType("jsonb");
            e.Property(x => x.Saved).HasColumnType("jsonb");
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<FoodItem>().WithMany().HasForeignKey(x => x.FoodItemId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Photo).WithOne(p => p.LabelScan).HasForeignKey<LabelScanPhoto>(p => p.LabelScanId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
        });
        builder.Entity<LabelScanPhoto>(e =>
        {
            e.HasQueryFilter(x => x.LabelScan.UserId == CurrentUserId);
            e.Property(x => x.ContentType).HasMaxLength(20);
        });
```

Check how `ParseRun` declares its filter (by `UserId == CurrentUserId` or a named filter) and match it.

- [ ] **Step 3: Create the migration.** `dotnet ef migrations add AddLabelScansAndSharedBy --project src/Plate.Web --output-dir Data/Migrations`. Then add the one-off backfill at the end of `Up`, after the column exists:

```csharp
            // Shared manual foods made before this change: credit whoever logged them first (SPEC 4).
            migrationBuilder.Sql("""
                UPDATE "FoodItems" f
                SET "AddedByUserId" = first."UserId"
                FROM (
                    SELECT DISTINCT ON ("FoodItemId") "FoodItemId", "UserId"
                    FROM "FoodLog"
                    WHERE "FoodItemId" IS NOT NULL
                    ORDER BY "FoodItemId", "CreatedAt"
                ) first
                WHERE f."Id" = first."FoodItemId" AND f."Source" = 'Manual' AND f."OwnerUserId" IS NULL;
                """);
```

Confirm the food log table name (`FoodLog` vs `FoodLogEntries`) in the model snapshot before running. The backfill includes soft-deleted entries on purpose: they still show who logged first.

- [ ] **Step 4: Write `LabelDataTests.cs`** on `PostgresFixture`:
  - `PrivateBarcodeDoesNotBlockAnotherUsersPrivateOne`: two users each insert a private `FoodItem` with barcode `5901234123457`; both save.
  - `TwoSharedFoodsCannotShareABarcode`: a second shared item with the same barcode throws `DbUpdateException`.
  - `PrivateAndSharedMayCoexist`: one private and one shared with the same barcode both save.
  - `ScanIsInvisibleToOtherUsers`: user A's `LabelScan` and photo aren't returned to user B through the query filters (use `UserScope` like `UserIsolationTests`).
  - `BackfillCreditsFirstLogger`: run the backfill SQL above against seeded rows and check `AddedByUserId`. Keep the SQL in an `internal const string` on the migration class so the test calls the same text.

- [ ] **Step 5: Run.** `dotnet test tests/Plate.Web.Tests --filter "LabelDataTests|MigrationTests"`. Expected: PASS. Then all Core and Web tests.

- [ ] **Step 6: Commit.** "Data: LabelScan, photo table, FoodItem.AddedByUserId and split barcode index"

---

### Task 4: Sharing choice, own-first lookup and the anonymous mark

**Files:**
- Modify: `src/Plate.Web/Features/Food/FoodItemService.cs` (`FoodMatch` record line 9, `SearchAsync` lines 26-44, `SearchForPickerAsync` SQL lines 60-82, `LookupBarcodeAsync` lines 98-118, `CacheAsync` lines 121-147, `CreateManualAsync` lines 149-195)
- Modify: `src/Plate.Web/Controllers/FoodController.cs:144-151` (`CreateItem`), `:207-217` (`Entry`)
- Modify: `src/Plate.Web/Views/Food/_ManualFoodForm.cshtml`, `Views/Food/Barcode.cshtml`, `Views/Food/Add.cshtml` (search rows near line 100 and manual tab), `Views/Food/Entry.cshtml:22-25`
- Modify: `src/Plate.Web/Resources/strings.bg.json` (and regenerate keys)
- Test: new `tests/Plate.Web.Tests/SharedFoodTests.cs`, `UserIsolationTests.cs`

**Interfaces:**
- Consumes: `FoodItem.AddedByUserId` (Task 3).
- Produces:
  - `FoodItemService.CreateManualAsync(string name, string? barcode, Per100g per100, decimal? servingGrams, bool share, CancellationToken ct)`
  - `static bool FoodItemService.AddedByAnother(FoodItem item, string? userId)`: `item.Source == FoodSource.Manual && item.OwnerUserId is null && item.AddedByUserId != userId`
  - `FoodMatch` gains a trailing `bool ByOther` column.
  - `_ManualFoodForm` model becomes `record ManualFoodFormModel(DateOnly Date, Meal Meal, string? Barcode, string? Name, bool BarcodeField, LabelValues? Label = null, int? LabelScanId = null, bool LabelAvailable = false, LocalText? LabelError = null, string? BarcodeValue = null)` in `ViewModels`. `Barcode` is a scanned code (hidden field, after a miss); `BarcodeValue` pre-fills the optional barcode field. Tasks 7 and 8 fill the label fields.
  - `CreateItem` accepts `bool share` and `int? labelScanId` (the second is used by Task 7).

- [ ] **Step 1: Write the failing tests** in `SharedFoodTests.cs`, using `AppFactory` and two signed-in clients, posting to `/food/items` with the antiforgery helper the other food tests use:
  - `SharedByDefaultIsFoundByOtherUsersScan`: A posts with barcode and `share=true`; B's `GET /food/barcode?code=...` shows the food and "Added by another user"; the response contains neither A's display name nor email.
  - `CreatorSeesNoMark`: A's own scan of the same barcode has no "Added by another user".
  - `UntickedStaysPrivate`: A posts with `share=false`; B's scan doesn't find it (falls to `FakeFoodDatabase`, not found) and B can save its own food for the same barcode.
  - `OwnPrivateBeatsShared`: B has a private item and a shared one exists for the same barcode; B's scan shows B's own.
  - `SearchLineShowsMark`: B searches the name; the row's sub-line has "Added by another user".
  - `EntrySheetShowsMark`: B logs it and opens `/food/entries/{id}`; the mark is there.
  - `OffAndUsdaFoodsShowNoMark`: a scan of a `FakeFoodDatabase` product and a USDA search hit have no mark.
  - `DeletedCreatorStillReadsAnotherUser`: set A's `AddedByUserId` to null (simulating `delete-user`); B still sees the mark.
  - `SharingRaceGivesValidationError` (Review Focus 4): A shares barcode X; then B posts a manual food with `share=true` for X (a stale form) and gets a 200 with "A food with that barcode is already shared. Scan it again to use it.", not a 500. Repeat with a forced `DbUpdateException`: insert the conflicting shared row directly between the service's check and save by calling `CreateManualAsync` twice in parallel from two scopes. Exactly one succeeds and the other gets the same validation message.
  - `NoBarcodeIsAlwaysPrivate`: posting `share=true` without a barcode creates a private item.

- [ ] **Step 2: Run them and see them fail.** `dotnet test tests/Plate.Web.Tests --filter SharedFoodTests`.

- [ ] **Step 3: Change the service.**

`LookupBarcodeAsync`, replacing `SingleOrDefaultAsync(f => f.Barcode == barcode, ct)`:

```csharp
        // The user's own food first, then a shared one (SPEC 4); the query filter hides other users' private foods.
        var cached = await db.FoodItems.Where(f => f.Barcode == barcode)
            .OrderBy(f => f.OwnerUserId == null)
            .FirstOrDefaultAsync(ct);
```

`CacheAsync`: the existing-row check becomes `f.Barcode == p.Barcode && f.OwnerUserId == null`, since only shared rows can conflict.

`CreateManualAsync` with the new `share` parameter:

```csharp
    /// <summary>
    /// A food typed in by hand. With a barcode and <paramref name="share"/> it is shared, so others who scan the same
    /// product get it; otherwise it belongs to this user only (SPEC 4).
    /// </summary>
    public async Task<FoodItem> CreateManualAsync(string name, string? barcode, Per100g per100, decimal? servingGrams, bool share, CancellationToken ct)
    {
        // ... existing name, Validate and barcode format checks unchanged ...
        var userId = currentUser.UserId ?? throw new InvalidOperationException("No signed-in user.");
        var shared = barcode is not null && share;
        if (barcode is not null && await db.FoodItems.IgnoreQueryFilters().AnyAsync(
                f => f.Barcode == barcode && (shared ? f.OwnerUserId == null : f.OwnerUserId == userId), ct))
        {
            throw new ValidationException(shared
                ? "A food with that barcode is already shared. Scan it again to use it."
                : "You already have a food with that barcode.");
        }

        var item = new FoodItem
        {
            OwnerUserId = shared ? null : userId,
            AddedByUserId = userId,
            // ... remaining fields unchanged ...
        };
        db.FoodItems.Add(item);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            throw new ValidationException(shared
                ? "A food with that barcode is already shared. Scan it again to use it."
                : "You already have a food with that barcode.");
        }

        return item;
    }

    /// <summary>A shared manual food someone else added (or whose account is gone). Says nothing about who.</summary>
    public static bool AddedByAnother(FoodItem item, string? userId) =>
        item.Source == FoodSource.Manual && item.OwnerUserId is null && item.AddedByUserId != userId;
```

In both search SQL statements add the column, and append `bool ByOther` to the `FoodMatch` record (last positional parameter, before `Score` if the SQL selects in order; keep SELECT order equal to the record order):

```sql
(f."Source" = 'Manual' AND f."OwnerUserId" IS NULL AND f."AddedByUserId" IS DISTINCT FROM {userId}) AS "ByOther",
```

In `SearchForPickerAsync`, which selects from a subquery, pass `"ByOther"` through the outer SELECT too. Update every `new FoodMatch(...)` call (grep for it in Web and tests) with `false`.

- [ ] **Step 4: Controller.** `CreateItem` gets `bool share = false, int? labelScanId = null` and passes `share` on. The Manual tab's barcode field posts an empty string when left blank, so the entry source becomes `string.IsNullOrWhiteSpace(barcode) ? EntrySource.Manual : EntrySource.Barcode`. HTML checkboxes send nothing when unticked, so the form sends a hidden `share=false` before the checkbox, and model binding takes the last value. `Entry` adds:

```csharp
            if (await foods.FindVisibleAsync(foodId, ct) is { } food)
            {
                ViewData["MatchedName"] = Localization.FoodNames.DisplayName(food);
                ViewData["AddedByAnother"] = FoodItemService.AddedByAnother(food, currentUser.UserId);
            }
```

with a new `FindVisibleAsync(int id, CancellationToken ct)` on the service returning the filtered `FoodItem?` (refactor `DisplayNameOrNullAsync` to use it). `FoodController` needs `ICurrentUser` (or whatever `FoodItemService` injects as `currentUser`) in its constructor.

- [ ] **Step 5: Views.**

`_ManualFoodForm.cshtml`: switch the model to `ManualFoodFormModel`. After the name row, when `Model.BarcodeField` is true (Manual tab and the Scan tab's label path), add an optional barcode row:

```cshtml
<div class="field-row"><label for="m-barcode">@L["Barcode"]</label><input id="m-barcode" class="num" type="text" name="barcode" value="@Model.BarcodeValue" inputmode="numeric" pattern="[0-9 -]{8,16}" autocomplete="off" placeholder="@L["Optional"]" data-barcode-input /></div>
```

Above Save, a share block that shows when there is a barcode. After a barcode miss it is rendered visible. On the Manual tab it is rendered `hidden` and toggled by Task 8's `label.js` when the barcode field gets a value; it doesn't depend on that script to submit correctly, since the server ignores `share` without a barcode:

```cshtml
@{ var hasBarcode = Model.Barcode is not null || !string.IsNullOrEmpty(Model.BarcodeValue); }
<div class="list share-row" data-share @(hasBarcode ? null : "hidden") style="margin-top: var(--s-5)">
    <input type="hidden" name="share" value="false" />
    <label class="field-row toggle-row"><span>@L["Share with other users"]</span><input type="checkbox" name="share" value="true" checked class="switch" /></label>
</div>
<p class="section-footer" data-share @(hasBarcode ? null : "hidden")>@L["Others who scan this barcode get this food instead of entering it again. Your name isn't shown."]</p>
```

Use the existing switch markup from Settings (the toggle fixed in 7f43eb5) so it looks the same; the above is the structure, not the exact class names. Remove the old "Barcode {0}. Saved for both of you…" footer.

`Barcode.cshtml`: below the eyebrow/title header, when `ViewData["AddedByAnother"] is true` (set in the `Barcode` action from `FoodItemService.AddedByAnother(item, currentUser.UserId)`):

```cshtml
<p class="footnote added-by">@Ui.Icon("people", "icon-sm")@L["Added by another user"]</p>
```

Use an icon that exists in `Ui.Icon`'s sprite; grep for the list and pick a person or people icon, or none.

`Add.cshtml` search row sub-line: append ` · @L["Added by another user"]` when `f.ByOther`.

`Entry.cshtml`: after the Food row, when `ViewData["AddedByAnother"] is true`, a row with `@L["Added by another user"]` in `row-value` style.

- [ ] **Step 6: Strings.** Add to `strings.bg.json`:
  - "Share with other users": "Сподели с другите потребители"
  - "Others who scan this barcode get this food instead of entering it again. Your name isn't shown.": "Който сканира този баркод, ще получи храната, без да я въвежда отново. Името ти не се показва."
  - "Added by another user": "Добавено от друг потребител"
  - "A food with that barcode is already shared. Scan it again to use it.": "Вече има споделена храна с този баркод. Сканирай го отново, за да я използваш."
  - "You already have a food with that barcode.": "Вече имаш храна с този баркод."
  - "Barcode": check whether it already exists before adding.

  Remove the now-unused "Barcode {0}. Saved for both of you, so it only needs entering once." from both files. Regenerate the keys.

- [ ] **Step 7: Run.** `SharedFoodTests`, `UserIsolationTests`, `FoodSearchTests`, `CatalogueTests`, `BulgarianCatalogueTests`, then all Core and Web. Expected: PASS. Run the E2E `FoodSearchTests` too, since the search markup changed.

- [ ] **Step 8: Commit.** "Share choice for barcode foods, own-first lookup and the anonymous Added by another user mark"

---

### Task 5: The label AI task and vision model catalog

**Files:**
- Modify: `src/Plate.Web/Integrations/OpenRouterModels.cs` (record line 12, `Parse` lines 64-99)
- Modify: `src/Plate.Web/Integrations/AiTasks.cs`
- Modify: `src/Plate.Web/Integrations/OpenRouterSettings.cs:67-102`
- Modify: `src/Plate.Web/Controllers/AdminAiController.cs:66-97`, `src/Plate.Web/Views/AdminAi/Index.cshtml` and `Model.cshtml` if they enumerate tasks by hand
- Modify: `src/Plate.Web/Cli/AiCommand.cs` (usage text and `set-model` slugs)
- Test: `tests/Plate.Web.Tests/OpenRouterTests.cs` (stub catalog + new tests)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `OpenRouterModel` gains a trailing `bool AcceptsImages = false`.
  - `AiTask.Label`, slug `label`, title "Label reader", setting `OpenRouter:Model:Label`.
  - `AiTasks.LabelOff` (`new(Off, "Plate", "Off: no label photos", 0, 0, false, null)`), `AiTasks.DefaultLabelModel = "google/gemini-3.5-flash-lite"`.
  - `OpenRouterSettings.GetModelAsync(AiTask.Label)` returns the stored value or `DefaultLabelModel`; `ListModelsAsync(AiTask.Label)` returns `[LabelOff, ..catalog.Where(m => m.AcceptsImages)]`.

- [ ] **Step 1: Failing tests.** In `OpenRouterTests.cs`, give the stub catalog's `google/gemini-3.5-flash-lite` entry `"input_modalities":["text","image"]` and leave `openai/gpt-x` text-only. Add:
  - `CatalogMarksImageInput`: `Parse` sets `AcceptsImages` true for gemini and false for gpt-x.
  - `LabelPickerListsOnlyVisionModels`: `ListModelsAsync(AiTask.Label)` is `[off, gemini]`.
  - `LabelDefaultsToGeminiFlashLite`: with nothing stored, `GetModelAsync(AiTask.Label)` is `google/gemini-3.5-flash-lite`.
  - `TextOnlyModelIsRefusedForLabels`: `SetModelAsync(AiTask.Label, "openai/gpt-x")` throws `ValidationException` "That model can't read labels."
  - `AdminCanPickLabelModel`: in `AdminTests` style, POST `/admin/ai/model/label` with `off` saves it.

- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Plate.Web.Tests --filter "OpenRouterCatalogTests|OpenRouterSettingsTests"`.

- [ ] **Step 3: Implement.**

`OpenRouterModels.Parse`: read `var input = Strings(m?["architecture"]?["input_modalities"]);` and pass `input.Contains("image")` as the new last constructor argument.

`AiTasks.cs`:

```csharp
    /// <summary>Reads nutrition labels from photos (OpenRouter vision models, SPEC 6.6).</summary>
    Label,
```

plus `Title` → `"Label reader"`, `OffLabel` → `"Off: no label photos"`, `SettingName` → `"OpenRouter:Model:Label"`, and the two constants above.

`OpenRouterSettings`:

```csharp
            AiTask.Label => AiTasks.DefaultLabelModel,
```

in `GetModelAsync`'s fallback switch (not `Off`: the spec's default is on), and in `ListModelsAsync`:

```csharp
        AiTask.Label => await catalog.ListAsync(ct) is { } vision ? [AiTasks.LabelOff, .. vision.Where(m => m.AcceptsImages)] : null,
```

and `AiTask.Label => "That model can't read labels."` in `SetModelAsync`'s message switch.

`AdminAiController.SetModel`: make the weekly branch explicit (`else if (t == AiTask.Weekly) { ... }`) and add a final `else { TempData.Notice("Model saved."); }`, so saving a label model works now. Task 6 inserts the label test branch before that `else`.

`AiCommand.cs`: add `label` to the usage line `ai set-model parser|matcher|onboarding|weekly|label <id>`.

- [ ] **Step 4: Run.** Same filter plus `AdminTests`, then all Core and Web. Expected: PASS.

- [ ] **Step 5: Commit.** "Label AI task with a vision-only model picker"

---

### Task 6: OpenRouterLabelReader

**Files:**
- Create: `src/Plate.Web/Integrations/OpenRouterLabelReader.cs`
- Create: `src/Plate.Web/Integrations/Assets/sample-label.jpg` (synthetic, made by a committed script, see Step 4) and include it as an embedded resource in `Plate.Web.csproj`
- Modify: `src/Plate.Web/Program.cs:117-153` (HttpClient + DI), `src/Plate.Web/Features/Admin/AiAdmin.cs` (`TestLabelAsync`), `Controllers/AdminAiController.cs` (Label branch), `Cli/AiCommand.cs` (`ai test [label]`)
- Test: `tests/Plate.Web.Tests/OpenRouterTests.cs` (`OpenRouterLabelReaderTests`), `tests/Plate.Web.Tests/FakeLabelReader.cs`

**Interfaces:**
- Consumes: `ILabelReader`, `LabelReading`, `LabelReadResult` (Task 2); `AiTask.Label`, `OpenRouterModel` (Task 5).
- Produces:
  - `OpenRouterLabelReader.ReadAsync(byte[] image, string contentType, string apiKey, string model, CancellationToken ct) : Task<LabelReadResult>`
  - `OpenRouterLabelReader.TestAsync(string apiKey, string model, CancellationToken ct) : Task<LocalText?>` reading the bundled sample.
  - `SwitchingLabelReader : ILabelReader` registered scoped.
  - `AiAdmin.TestLabelAsync(CancellationToken ct) : Task<LocalText?>` (null when it works or is off).
  - Test double `FakeLabelReader : ILabelReader` with a settable `Next` result and a `Calls` counter.

- [ ] **Step 1: Failing tests** (`OpenRouterLabelReaderTests`, with `StubOpenRouter` gaining `public OpenRouterLabelReader Label() => new(Client(), Catalog(), NullLogger<OpenRouterLabelReader>.Instance);`):
  - `SendsImageAsDataUrlWithStrictSchema`: after `ReadAsync([0xFF,0xD8,0xFF,1], "image/jpeg", "k", "google/gemini-3.5-flash-lite")`, `SentBody["messages"][1]["content"][1]["image_url"]["url"]` starts with `data:image/jpeg;base64,/9j/`; `response_format.json_schema.strict` is true; `provider.require_parameters` is true; `reasoning.effort` is `minimal` (the stub lists it as mandatory with minimal lowest).
  - `ReadsAnswer`: a completion of `{"is_label":true,"name":"Айрян","brand":null,"basis":"per_100ml","serving_grams":null,"energy_kcal":38,"energy_kj":159,"protein":1.8,"carbs":2.6,"fat":2.2,"saturated_fat":1.4,"fiber":null}` gives a `LabelReading` with `Basis == Per100ml`, `Protein == 1.8m`, and `Raw` equal to the content string.
  - `UnreadableAnswerFails`: content `"not json"` gives `Error` "Couldn't read the label. Enter the values by hand." with `Raw` kept.
  - `RetriesOnceOn5xx`: a stub that returns 502 then 200 gives a reading, and the stub saw 2 calls. Add a `Queue<HttpStatusCode>` option to `StubOpenRouter` for this.
  - `DoesNotRetryTimeout`: a handler that delays past a 50 ms test timeout (make `AttemptTimeout` an `internal static TimeSpan` set by the test) gives the error after 1 call.

- [ ] **Step 2: Run and see them fail.**

- [ ] **Step 3: Implement** `OpenRouterLabelReader.cs`, copying the structure of `OpenRouterWeeklySummaryWriter` (same logging, `Truncate`, `Str` helpers):

```csharp
/// <summary>
/// Reads a nutrition label photo with an OpenRouter vision model and strict structured output (SPEC 6.6). The model
/// transcribes; LabelValues.Normalize does the arithmetic.
/// </summary>
public class OpenRouterLabelReader(HttpClient http, OpenRouterModels catalog, ILogger<OpenRouterLabelReader> log)
{
    internal static TimeSpan AttemptTimeout = TimeSpan.FromSeconds(20);
    private static readonly LocalText Unreadable = "Couldn't read the label. Enter the values by hand.";

    private const string Instructions = """
        You read the nutrition table on a food package from a photo, for a calorie tracker. Transcribe; never calculate,
        convert or estimate a number.

        - Use the column per 100 g, or per 100 ml for drinks. Only if the label has neither, use the per-serving column
          and give the serving weight in grams in serving_grams. basis says which column you used.
        - serving_grams: the portion weight when it is printed (for example "1 порция = 30 g"), else null.
        - A value that isn't printed or can't be read clearly is null. Never guess.
        - Read decimal commas as decimals ("12,5" is 12.5). "<0,5" or "<0.1" is the number after "<".
        - energy_kj and energy_kcal are the two energy figures; either may be missing.
        - Labels are usually Bulgarian: Енергийна стойност (energy), Мазнини (fat), от които наситени мастни киселини
          (saturated_fat), Въглехидрати (carbs), от които захари (sugars: NOT carbs, ignore), Фибри or Хранителни
          влакна (fiber), Белтъчини (protein), Сол (salt, ignore). Other EU languages use the same rows.
        - name and brand only when printed in this photo, as written; otherwise null.
        - is_label is false when the photo doesn't show a nutrition table.
        """;

    private static readonly JsonObject Schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "is_label": { "type": "boolean" },
            "name": { "type": ["string", "null"] },
            "brand": { "type": ["string", "null"] },
            "basis": { "type": "string", "enum": ["per_100g", "per_100ml", "per_serving"] },
            "serving_grams": { "type": ["number", "null"] },
            "energy_kcal": { "type": ["number", "null"] },
            "energy_kj": { "type": ["number", "null"] },
            "protein": { "type": ["number", "null"] },
            "carbs": { "type": ["number", "null"] },
            "fat": { "type": ["number", "null"] },
            "saturated_fat": { "type": ["number", "null"] },
            "fiber": { "type": ["number", "null"] }
          },
          "required": ["is_label", "name", "brand", "basis", "serving_grams", "energy_kcal", "energy_kj", "protein", "carbs", "fat", "saturated_fat", "fiber"],
          "additionalProperties": false
        }
        """)!.AsObject();

    public async Task<LabelReadResult> ReadAsync(byte[] image, string contentType, string apiKey, string model, CancellationToken ct)
    {
        var body = await BodyAsync(image, contentType, model, ct);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AttemptTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = JsonContent.Create(body) };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                using var response = await http.SendAsync(request, timeout.Token);
                if ((int)response.StatusCode >= 500 && attempt == 1)
                {
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    log.LogWarning("OpenRouter returned {Status} for {Model} reading a label: {Body}", (int)response.StatusCode, model, Truncate(await response.Content.ReadAsStringAsync(ct)));
                    return LabelReadResult.Failed(Unreadable, model);
                }

                return Read(await response.Content.ReadFromJsonAsync<JsonNode>(timeout.Token), model);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // A timeout is not retried: the user is waiting with the form open (SPEC 6.6).
                return LabelReadResult.Failed(Unreadable, model);
            }
            catch (HttpRequestException ex)
            {
                log.LogWarning(ex, "OpenRouter request failed reading a label");
                if (attempt == 2)
                {
                    return LabelReadResult.Failed(Unreadable, model);
                }
            }
        }

        return LabelReadResult.Failed(Unreadable, model);
    }

    private async Task<JsonObject> BodyAsync(byte[] image, string contentType, string model, CancellationToken ct)
    {
        var info = await catalog.FindAsync(model, ct);
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = Instructions },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "text", ["text"] = "Read this nutrition label." },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = $"data:{contentType};base64,{Convert.ToBase64String(image)}" },
                        }),
                }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "nutrition_label", ["strict"] = true, ["schema"] = Schema.DeepClone() },
            },
            ["provider"] = new JsonObject { ["require_parameters"] = true },
        };
        if (info?.SupportsTemperature == true)
        {
            body["temperature"] = 0;
        }

        if (info?.LowestEffort is { } effort)
        {
            body["reasoning"] = new JsonObject { ["effort"] = effort };
        }

        return body;
    }

    internal static LabelReadResult Read(JsonNode? response, string model)
    {
        var json = response?["choices"]?[0]?["message"]?["content"] is JsonValue content && content.TryGetValue<string>(out var c) ? c : null;
        try
        {
            if (string.IsNullOrWhiteSpace(json) || JsonNode.Parse(json) is not JsonObject o || o["is_label"] is not JsonValue isLabel)
            {
                return LabelReadResult.Failed(Unreadable, model, json);
            }

            var basis = Str(o["basis"]) switch
            {
                "per_100ml" => LabelBasis.Per100ml,
                "per_serving" => LabelBasis.PerServing,
                _ => LabelBasis.Per100g,
            };
            return new LabelReadResult(
                new LabelReading(isLabel.GetValue<bool>(), Str(o["name"]), Str(o["brand"]), basis, Num(o["serving_grams"]),
                    Num(o["energy_kcal"]), Num(o["energy_kj"]), Num(o["protein"]), Num(o["carbs"]), Num(o["fat"]), Num(o["saturated_fat"]), Num(o["fiber"])),
                json, null, model);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return LabelReadResult.Failed(Unreadable, model, json);
        }
    }

    private static decimal? Num(JsonNode? node) => node is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : null;
    // Str and Truncate as in OpenRouterWeeklySummaryWriter.
}

/// <summary>The label model when a key is set and it isn't off; otherwise label photos are hidden (SPEC 6.6).</summary>
public class SwitchingLabelReader(OpenRouterSettings settings, IServiceProvider services) : ILabelReader
{
    public async Task<bool> IsAvailableAsync(CancellationToken ct) =>
        await settings.GetModelAsync(AiTask.Label, ct) != AiTasks.Off && await settings.GetKeyAsync(ct) is not null;

    public async Task<LabelReadResult> ReadAsync(byte[] image, string contentType, CancellationToken ct)
    {
        var model = await settings.GetModelAsync(AiTask.Label, ct);
        if (model == AiTasks.Off || await settings.GetKeyAsync(ct) is not { } key)
        {
            return LabelReadResult.Failed("Couldn't read the label. Enter the values by hand.");
        }

        return await services.GetRequiredService<OpenRouterLabelReader>().ReadAsync(image, contentType, key, model, ct);
    }
}
```

`TestAsync` reads the bundled sample (Step 4) and checks one number:

```csharp
    public const decimal SampleKcal = 250;

    /// <summary>Reads the bundled sample label: null when the model reads its energy right, otherwise what went wrong.</summary>
    public async Task<LocalText?> TestAsync(string apiKey, string model, CancellationToken ct)
    {
        await using var stream = typeof(OpenRouterLabelReader).Assembly.GetManifestResourceStream("Plate.Web.Integrations.Assets.sample-label.jpg")
            ?? throw new InvalidOperationException("The sample label isn't embedded.");
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        var result = await ReadAsync(buffer.ToArray(), "image/jpeg", apiKey, model, ct);
        if (result.Reading is not { } reading)
        {
            return result.Error;
        }

        var kcal = LabelValues.Normalize(reading).Values?.Kcal;
        return kcal == SampleKcal ? null : new LocalText("The model misread the sample label (read {0} kcal, expected {1}).", kcal?.ToString(CultureInfo.InvariantCulture) ?? "nothing", SampleKcal);
    }
```

Check the manifest resource name with `dotnet build` and `GetManifestResourceNames()` if the folder name maps differently; the embed is `<EmbeddedResource Include="Integrations/Assets/sample-label.jpg" />`. Add the new message to `strings.bg.json` only if `CatalogueTests` asks for it (admin and CLI text is English).

`Program.cs`: register the typed client next to the others and the switch:

```csharp
builder.Services.AddHttpClient<OpenRouterLabelReader>(c =>
{
    OpenRouterClient(c);
    c.Timeout = TimeSpan.FromSeconds(50);
});
builder.Services.AddScoped<ILabelReader, SwitchingLabelReader>();
```

Match the existing clients' timeout configuration if they set one differently.

`AiAdmin.TestLabelAsync` follows `TestWeeklyAsync`. `AdminAiController.SetModel` gets `else if (t == AiTask.Label) Report(await ai.TestLabelAsync(ct), "Model saved: label reading works.", "Model saved, but it doesn't work yet: {0}");`. `AiCommand` `test`: with `["test", "label"]` run only the label test and print `Label: works or off.` or the error. Bare `test` includes it too.

`FakeLabelReader` in the test project:

```csharp
public sealed class FakeLabelReader : ILabelReader
{
    public static readonly LabelReading Yogurt = new(true, "Кисело мляко 3.6%", "Верея", LabelBasis.Per100g, 400, 62, 259, 3.2m, 4.1m, 3.6m, 2.3m, null);

    public LabelReadResult Next { get; set; } = new(Yogurt, "{}", null, "test/label");
    public int Calls { get; private set; }

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<LabelReadResult> ReadAsync(byte[] image, string contentType, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(Next);
    }
}
```

- [ ] **Step 4: The sample label.** Add `tools/labels/make-sample.mjs` that writes a 900×700 JPEG nutrition table in Bulgarian with known values (energy 1046 kJ / 250 kcal, fat 10 g, saturated 4 g, carbs 30 g, sugars 12 g, fiber 2 g, protein 10 g, salt 0.8 g) using Playwright's Chromium screenshot of a small HTML table (Playwright is already a dev dependency of the E2E project; no new package). Commit the script and the generated `sample-label.jpg`. `TestAsync` expects 250 kcal.

- [ ] **Step 5: Run.** `OpenRouterLabelReaderTests`, `AdminTests`, `AccountCliTests` if `ai` CLI tests live there, then all Core and Web. Expected: PASS.

- [ ] **Step 6: Commit.** "OpenRouterLabelReader with strict schema, admin test and ai test label"

---

### Task 7: LabelScanService and the endpoint

**Files:**
- Create: `src/Plate.Web/Features/Food/LabelScanService.cs`
- Create: `src/Plate.Web/Features/ImageSniffer.cs` (moved out of `FeedbackService.ImageType`)
- Modify: `src/Plate.Web/Features/Feedback/FeedbackService.cs:225-246` (use `ImageSniffer`)
- Modify: `src/Plate.Web/Features/FeatureExceptionFilter.cs:8-20` (`RateLimits.AiLabel = "ai-label"`), `Program.cs:299-303` (policy), DI for the service
- Modify: `src/Plate.Web/Controllers/FoodController.cs` (new `Label` action; `CreateItem` records the save)
- Test: new `tests/Plate.Web.Tests/LabelScanTests.cs`, `UserIsolationTests.cs`

**Interfaces:**
- Consumes: `ILabelReader`, `LabelValues` (Task 2), entities (Task 3), `CreateManualAsync(..., share, ...)` and `ManualFoodFormModel` (Task 4), `FakeLabelReader` (Task 6).
- Produces:
  - `ImageSniffer.ContentType(ReadOnlySpan<byte> bytes) : string?` (jpeg, png, webp or null)
  - `record LabelScanResult(int ScanId, LabelValues? Values, LocalText? Error)`
  - `LabelScanService.ReadAsync(byte[] bytes, int width, int height, CancellationToken ct) : Task<LabelScanResult>`
  - `LabelScanService.RecordSaveAsync(int? scanId, FoodItem saved, CancellationToken ct) : Task`
  - `LabelScanService.IsAvailableAsync(CancellationToken ct) : Task<bool>`
  - `POST /food/label` (multipart: `photo`, `width`, `height`, `date`, `meal`, `barcode?`, `barcodeField`, `name?`) returning the `_ManualFoodForm` partial filled.

- [ ] **Step 1: Failing tests** (`LabelScanTests`, `AppFactory` with `services: s => s.AddSingleton<FakeLabelReader>().AddScoped<ILabelReader>(sp => sp.GetRequiredService<FakeLabelReader>())`):
  - `FillsFormFromPhoto`: POST a small valid JPEG (the JPEG magic bytes followed by padding are enough, since only the first bytes are sniffed) and the response contains `value="62"` for energy, `value="3.2"` for protein, the banner "Read from the photo. Check each value against the label.", "One serving" `400`, and a hidden `labelScanId`.
  - `StoresScanAndPhoto`: one `LabelScan` row with `Model == "test/label"`, non-null `Values`, and a `LabelScanPhoto` of the posted length.
  - `FailedReadStoresErrorAndKeepsForm`: `Next = LabelReadResult.Failed(...)` gives the "Couldn't read the label. Enter the values by hand." message, a row with `Error` set, and the posted `name` still in the name field.
  - `NotALabelShowsNotFound`: `Next` with `IsLabel: false` shows "Couldn't find a nutrition table in that photo."
  - `RejectsNonImage`: bytes `"hello"` give 400 "Attach a photo (JPEG, PNG or WebP).", reader not called.
  - `AcceptsPhotoOver1MB`: a 1.5 MB JPEG is accepted (proves the per-endpoint limit beats the 1 MB default); a 2.5 MB one gets "That photo is too large." and the reader isn't called.
  - `OtherEndpointsKeep1MBLimit`: a 1.5 MB POST to `/food/quick` is refused (413 or the app's equivalent).
  - `RateLimitedAt21`: with `RateLimits:AiLabelPerWindow` set to 2 in the factory settings, the third POST returns 429.
  - `HiddenWithoutKey`: with the real switching reader and no key, `/food/add?tab=manual` has no `data-label` button and `POST /food/label` returns 404.
  - `SaveRecordsEditedFlag`: read, then POST `/food/items` with `labelScanId` and protein changed to 4.0; the scan has `FoodItemId` set, `Saved` non-null and `Edited == true`. Saving unchanged values gives `Edited == false`.
  - `ForeignScanIdIsIgnored` (also in `UserIsolationTests`): B saves with A's `labelScanId`; the food saves and A's scan is unchanged.
  - `LabelFormUsesDotDecimalsInBulgarian` (Review Focus 5): a Bulgarian account's filled form has `value="3.2"`, not `3,2`.

- [ ] **Step 2: Run and see them fail.**

- [ ] **Step 3: `ImageSniffer`.** Move the three-way magic-byte check from `FeedbackService.ImageType` into:

```csharp
namespace Plate.Web.Features;

/// <summary>An upload's real type from its first bytes, never from what the browser declared.</summary>
public static class ImageSniffer
{
    public static string? ContentType(ReadOnlySpan<byte> b) =>
        b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "image/jpeg"
        : b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ? "image/png"
        : b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8) ? "image/webp"
        : null;
}
```

and have `FeedbackService` call it. `FeedbackTests` must stay green.

- [ ] **Step 4: `LabelScanService`.**

```csharp
/// <summary>Reads a label photo, stores the scan with its photo, and records what the user saved (SPEC 6.6).</summary>
public class LabelScanService(AppDbContext db, ILabelReader reader, ICurrentUser currentUser, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<bool> IsAvailableAsync(CancellationToken ct) => reader.IsAvailableAsync(ct);

    public async Task<LabelScanResult> ReadAsync(byte[] bytes, int width, int height, CancellationToken ct)
    {
        if (bytes.Length > LabelScanPhoto.MaxBytes)
        {
            throw new ValidationException("That photo is too large.");
        }

        var type = ImageSniffer.ContentType(bytes) ?? throw new ValidationException("Attach a photo (JPEG, PNG or WebP).");
        var started = time.GetTimestamp();
        var result = await reader.ReadAsync(bytes, type, ct);
        var outcome = result.Reading is { } reading ? LabelValues.Normalize(reading) : new LabelOutcome(null, result.Error);
        var scan = new LabelScan
        {
            UserId = currentUser.UserId ?? throw new InvalidOperationException("No signed-in user."),
            CreatedAt = time.GetUtcNow(),
            Model = result.Model ?? "",
            LatencyMs = (int)time.GetElapsedTime(started).TotalMilliseconds,
            Error = (result.Error ?? outcome.Error)?.ToString(),
            Reading = result.Raw is null ? null : ValidJsonOrNull(result.Raw),
            Values = outcome.Values is null ? null : JsonSerializer.Serialize(StoredLabelValues.From(outcome.Values), Json),
            Photo = new LabelScanPhoto { ContentType = type, Width = Math.Clamp(width, 0, 10_000), Height = Math.Clamp(height, 0, 10_000), Bytes = bytes },
        };
        db.LabelScans.Add(scan);
        await db.SaveChangesAsync(ct);
        return new LabelScanResult(scan.Id, outcome.Values, outcome.Error);
    }

    /// <summary>A missing or foreign scan id is ignored: it never blocks saving food.</summary>
    public async Task RecordSaveAsync(int? scanId, FoodItem saved, CancellationToken ct)
    {
        if (scanId is null || await db.LabelScans.SingleOrDefaultAsync(s => s.Id == scanId, ct) is not { Values: { } json } scan)
        {
            return;
        }

        var suggested = JsonSerializer.Deserialize<StoredLabelValues>(json, Json)!.ToValues();
        scan.FoodItemId = saved.Id;
        scan.Saved = JsonSerializer.Serialize(new { saved.KcalPer100g, saved.ProteinPer100g, saved.CarbsPer100g, saved.FatPer100g, saved.SaturatedFatPer100g, saved.FiberPer100g, saved.DefaultServingGrams }, Json);
        scan.Edited = LabelValues.Edited(suggested, saved.Per100(), saved.DefaultServingGrams);
        await db.SaveChangesAsync(ct);
    }

    private static string? ValidJsonOrNull(string raw)
    {
        try { JsonDocument.Parse(raw).Dispose(); return raw; }
        catch (JsonException) { return JsonSerializer.Serialize(new { unreadable = raw.Length > 2000 ? raw[..2000] : raw }); }
    }
}

public record LabelScanResult(int ScanId, LabelValues? Values, LocalText? Error);

/// <summary>LabelValues as stored in LabelScan.Values: notes as their English text, since LocalText's arguments don't round-trip.</summary>
internal record StoredLabelValues(
    string? Name, string? Brand, decimal? Kcal, decimal? Protein, decimal? Carbs, decimal? Fat, decimal? SaturatedFat, decimal? Fiber,
    decimal? ServingGrams, LabelField Converted, string[] Notes, string[] Warnings)
{
    public static StoredLabelValues From(LabelValues v) => new(v.Name, v.Brand, v.Kcal, v.Protein, v.Carbs, v.Fat, v.SaturatedFat, v.Fiber,
        v.ServingGrams, v.Converted, v.Notes.Select(n => n.ToString()).ToArray(), v.Warnings.Select(w => w.ToString()).ToArray());

    public LabelValues ToValues() => new(Name, Brand, Kcal, Protein, Carbs, Fat, SaturatedFat, Fiber, ServingGrams, Converted, [], []);
}
```

Use whatever `ICurrentUser` type `FoodItemService` uses for `currentUser`.

Register `builder.Services.AddScoped<LabelScanService>();`.

- [ ] **Step 5: Rate limit.** `RateLimits.AiLabel = "ai-label"` and in `Program.cs`:

```csharp
    o.AddPolicy(RateLimits.AiLabel, http => PerUser(http, config.GetValue("RateLimits:AiLabelPerWindow", 20), TimeSpan.FromMinutes(10)));
```

- [ ] **Step 6: Endpoint.** In `FoodController` (inject `LabelScanService labels`):

```csharp
    [HttpPost("label")]
    [EnableRateLimiting(RateLimits.AiLabel)]
    [RequestSizeLimit(3 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 3 * 1024 * 1024)]
    public async Task<IActionResult> Label(DateOnly date, Meal meal, string? barcode, bool barcodeField, string? name, int width, int height, IFormFile? photo, CancellationToken ct)
    {
        if (!await labels.IsAvailableAsync(ct))
        {
            return NotFound();
        }

        if (photo is null || photo.Length == 0)
        {
            throw new ValidationException("Attach a photo (JPEG, PNG or WebP).");
        }

        if (photo.Length > LabelScanPhoto.MaxBytes)
        {
            throw new ValidationException("That photo is too large.");
        }

        using var buffer = new MemoryStream((int)photo.Length);
        await photo.CopyToAsync(buffer, ct);
        var result = await labels.ReadAsync(buffer.ToArray(), width, height, ct);
        var form = new ManualFoodFormModel(date, meal, barcodeField ? null : barcode, name, barcodeField,
            Label: result.Values, LabelScanId: result.ScanId, LabelAvailable: true, LabelError: result.Error,
            BarcodeValue: barcodeField ? barcode : null);
        return PartialView("_ManualFoodForm", form);
    }
```

Check how `FeatureExceptionFilter` renders a `ValidationException` for htmx requests and make sure the 400 body is something `label.js` can show (Task 8 reads the text of `[role=alert]` from it, or the response text).

`CreateItem`: after `CreateManualAsync`, call `await labels.RecordSaveAsync(labelScanId, item, ct);`.

- [ ] **Step 7: Run.** `LabelScanTests`, `UserIsolationTests`, `FeedbackTests`, `HttpSecurityTests`, then all Core and Web. Expected: PASS.

- [ ] **Step 8: Commit.** "LabelScanService and POST /food/label with size and rate limits"

---

### Task 8: The form UI (server side)

**Files:**
- Modify: `src/Plate.Web/Views/Food/_ManualFoodForm.cshtml`, `Views/Food/Add.cshtml` (manual tab and Scan tab), `Views/Food/Barcode.cshtml:49` (partial call), `src/Plate.Web/ViewModels/` (the `ManualFoodFormModel` record from Task 4), `Controllers/FoodController.cs` (`Add` and `Barcode` set `LabelAvailable`)
- Modify: `src/Plate.Web/wwwroot/css/app.css` (banner and highlight)
- Modify: `Resources/strings.bg.json`
- Test: `tests/Plate.Web.Tests/LabelScanTests.cs` (markup tests)

**Interfaces:**
- Consumes: `ManualFoodFormModel`, `LabelValues`, `LabelScanService.IsAvailableAsync`.
- Produces: markup hooks for Task 9's `label.js`: `data-label-form` on the `<form>`, a `data-label` button wrapper containing `<input type="file" accept="image/*" data-label-photo>`, `data-label-status`, `data-barcode-input`, `data-share`; and on the Scan tab a `data-label-start` button plus an empty `#label-form-slot`.

- [ ] **Step 1: Failing markup tests** in `LabelScanTests`:
  - `ManualTabHasPhotoButtonAndBarcodeField`: `/food/add?tab=manual` contains `data-label-photo`, `accept="image/*"`, no `capture=`, `data-barcode-input`, and the privacy line "The photo is sent to an AI model through OpenRouter and kept for 30 days so misreads can be checked."
  - `BarcodeMissHasPhotoButtonAndVisibleShare`: `/food/barcode?code=<unknown valid code>` contains `data-label-photo` and a `data-share` block without `hidden`.
  - `ScanTabHasPhotographLabel`: `/food/add?tab=barcode` contains "Photograph a label" and `#label-form-slot`.
  - `ConvertedFieldsAreMarked`: a per-serving `FakeLabelReader.Next` gives inputs with `class="... is-converted"` for kcal and protein, and the notes and warnings listed in the banner.

- [ ] **Step 2: Run and see them fail.**

- [ ] **Step 3: Markup.** In `_ManualFoodForm.cshtml`:
  - Wrap the whole form in `<div id="manual-food" data-label-form>` so htmx and `label.js` can swap it.
  - At the top, when `Model.LabelAvailable`:

```cshtml
<div class="label-pick" data-label>
    <label class="btn btn-secondary btn-block">@Ui.Icon("photo")@L["Read the label from a photo"]
        <input type="file" accept="image/*" class="sr-only" data-label-photo />
    </label>
    <p class="section-footer">@L["The photo is sent to an AI model through OpenRouter and kept for 30 days so misreads can be checked."]</p>
    <p class="thinking" role="status" data-label-status><i></i><i></i><i></i><span>@L["Reading the label…"]</span></p>
</div>
```

  - When `Model.Label is { } v`: a banner `<div class="banner" role="status">` with "Read from the photo. Check each value against the label." and each `v.Notes` and `v.Warnings` item (`@L[note]`). When `Model.LabelError is { } e`: `<div class="banner is-error" role="alert">@L[e]</div>`.
  - Each number input gets `value="@Fmt(v?.Kcal)"` with a helper that formats with `CultureInfo.InvariantCulture` (Review Focus 5), and `class="num@(v?.Converted.HasFlag(LabelField.Kcal) == true ? " is-converted" : "")@(v is not null && v.Kcal is null ? " is-missing" : "")"`. The name input's value is `Model.Name ?? v?.Name`: a posted name wins (Review Focus 3). Serving and "You ate" get `v?.ServingGrams`.
  - A hidden `<input type="hidden" name="labelScanId" value="@Model.LabelScanId" />` when set.

  In `Add.cshtml`'s manual case, render the partial with `BarcodeField: true` and `LabelAvailable` from the view model (add `bool LabelAvailable` to `FoodAddViewModel`, filled in `FoodController.Add` from `labels.IsAvailableAsync`). In the barcode case, under the "Scan a barcode" button, when available:

```cshtml
<label class="btn btn-secondary" data-label-start>@Ui.Icon("photo")@L["Photograph a label"]
    <input type="file" accept="image/*" class="sr-only" data-label-photo />
</label>
<div id="label-form-slot" data-label-slot data-date="@date" data-meal="@Model.Meal"></div>
```

  `Barcode.cshtml:49`: build the `ManualFoodFormModel` with `Barcode: Model.Code`, `BarcodeField: false`, `LabelAvailable` from the view model (`BarcodeViewModel` gains it).

- [ ] **Step 4: CSS.** In `app.css` next to `.composer`:

```css
.field-row input.is-converted { color: var(--accent-text); }
.field-row input.is-missing { box-shadow: inset 0 -1.5px 0 var(--warning, var(--accent)); }
.label-pick { margin-bottom: var(--s-5); }
.label-pick.is-busy .thinking { display: flex; }
```

Use the existing colour tokens; check `:root` for the warning token name.

- [ ] **Step 5: Strings.** Add to `strings.bg.json`:
  - "Read the label from a photo": "Прочети етикета от снимка"
  - "Photograph a label": "Снимай етикет"
  - "The photo is sent to an AI model through OpenRouter and kept for 30 days so misreads can be checked.": "Снимката се изпраща до AI модел през OpenRouter и се пази 30 дни, за да могат грешките да се проверят."
  - "Reading the label…": "Чета етикета…"
  - "Read from the photo. Check each value against the label.": "Прочетено от снимката. Провери всяка стойност спрямо етикета."
  - "Couldn't read the label. Enter the values by hand.": "Етикетът не можа да се прочете. Въведи стойностите ръчно."
  - "Couldn't find a nutrition table in that photo.": "На снимката няма таблица с хранителни стойности."
  - "The label only gives values per serving. Enter them by hand or photograph the per-100 g column.": "Етикетът дава стойности само за порция. Въведи ги ръчно или снимай колоната за 100 g."
  - "The label's kJ and kcal disagree; the kcal value was used.": "kJ и kcal на етикета не съвпадат; използвани са kcal."
  - "Per 100 ml, used as per 100 g.": "За 100 ml, използвано като за 100 g."
  - "Some values were outside what a food can have and were left empty.": "Някои стойности са извън възможното за храна и са оставени празни."
  - "Protein, carbs and fat add up to more than 100 g. Check them.": "Белтъчините, въглехидратите и мазнините са повече от 100 g общо. Провери ги."
  - "Saturated fat is more than fat. Check both.": "Наситените мазнини са повече от мазнините. Провери и двете."
  - "The calories don't match the protein, carbs and fat. Check the energy row.": "Калориите не отговарят на белтъчините, въглехидратите и мазнините. Провери реда за енергия."
  - "Attach a photo (JPEG, PNG or WebP).": "Прикачи снимка (JPEG, PNG или WebP)."
  - "That photo is too large.": "Снимката е твърде голяма."
  - "Needs a connection": "Нужна е връзка"

  Regenerate keys; run `CatalogueTests` and `BulgarianCatalogueTests`.

- [ ] **Step 6: Run.** `LabelScanTests`, `ViewHygieneTests`, `CatalogueTests`, `BulgarianCatalogueTests`, then all Core and Web. Expected: PASS.

- [ ] **Step 7: Commit.** "Manual food form: label photo button, filled values, Photograph a label on Scan"

---

### Task 9: The client (resize, barcode from photo, swap)

**Files:**
- Create: `src/Plate.Web/wwwroot/js/image.js`
- Create: `src/Plate.Web/wwwroot/js/label.js`
- Modify: `src/Plate.Web/wwwroot/js/feedback.js:12-13, 253-280` (use `image.js`)
- Modify: `src/Plate.Web/Views/Shared/_Layout.cshtml:55-57` (load `image.js` before `feedback.js`), `Views/Food/Add.cshtml` and `Views/Food/Barcode.cshtml` (load `label.js`, and the barcode-detector ponyfill on the manual and barcode-miss pages too)
- Modify: `src/Plate.Web/wwwroot/sw.js:15-25` (precache both files)
- Modify: `tests/Plate.E2E.Tests/AiStubs.cs` (a `StubLabelReader`), `E2EFixture.cs:54-56` (register it on the AI app)
- Test: new `tests/Plate.E2E.Tests/LabelPhotoTests.cs`, fixture images under `tests/Plate.E2E.Tests/Fixtures/`

**Interfaces:**
- Consumes: Task 8's markup hooks; `POST /food/label`.
- Produces: `window.gymImage.shrink(file, { maxSide, quality, maxBytes, steps }) : Promise<{ blob, width, height }>` (throws on decode failure or when it can't fit).

- [ ] **Step 1: `image.js`.** Move `shrink` out of `feedback.js` and parameterise it:

```js
// Downscales a picked image on the phone (SPEC 6.6, 11): drawn no larger than maxSide as JPEG, stepping quality and
// size down until it fits maxBytes. An <img> decodes whatever the browser can show, HEIC included on Safari.
window.gymImage = {
    async shrink(file, { maxSide, quality, maxBytes, steps }) {
        const url = URL.createObjectURL(file);
        try {
            const img = new Image();
            img.src = url;
            await img.decode();
            for (const [side, q] of [[maxSide, quality], ...steps]) {
                const scale = Math.min(1, side / Math.max(img.naturalWidth, img.naturalHeight));
                const canvas = document.createElement("canvas");
                canvas.width = Math.round(img.naturalWidth * scale);
                canvas.height = Math.round(img.naturalHeight * scale);
                canvas.getContext("2d").drawImage(img, 0, 0, canvas.width, canvas.height);
                const blob = await new Promise((resolve) => canvas.toBlob(resolve, "image/jpeg", q));
                if (!blob) {
                    throw new Error("encode");
                }
                if (blob.size <= maxBytes) {
                    return { blob, width: canvas.width, height: canvas.height };
                }
            }
            throw new Error("too large");
        } finally {
            URL.revokeObjectURL(url);
        }
    },
};
```

In `feedback.js`, `shrink(file)` becomes `window.gymImage.shrink(file, { maxSide: MAX_SIDE, quality: JPEG_QUALITY, maxBytes: MAX_BYTES, steps: [[MAX_SIDE, 0.6], [1200, 0.6], [900, 0.5]] })`. Note the old code returned `null` when `toBlob` failed; check the caller's null handling still shows "Couldn't read that picture. Try a screenshot." when it now throws.

- [ ] **Step 2: `label.js`.**

```js
// Label photos for the manual food form (SPEC 6.6): shrink on the phone, look for a barcode in the same photo,
// send it to /food/label and swap in the filled form. Only the latest photo's answer is used.
window.gymModule("label", (root) => {
    "use strict";

    let pending = null;

    async function barcodeIn(blob) {
        const api = window.BarcodeDetectionAPI;
        if (!api) {
            return null;
        }
        try {
            const detector = new api.BarcodeDetector({ formats: ["ean_13", "ean_8", "upc_a", "upc_e"] });
            const codes = await detector.detect(await createImageBitmap(blob));
            return codes.length ? codes[0].rawValue : null;
        } catch {
            return null;
        }
    }

    function formOf(input) {
        return input.closest("[data-label-form]");
    }

    async function read(input, slot) {
        const file = input.files && input.files[0];
        input.value = "";
        if (!file) {
            return;
        }
        if (!navigator.onLine) {
            window.gymToast(t("Needs a connection"));
            return;
        }

        pending?.abort();
        const controller = new AbortController();
        pending = controller;
        const host = slot || formOf(input);
        const busy = host.querySelector("[data-label]") || host;
        busy.classList.add("is-busy");
        try {
            let shot;
            try {
                shot = await window.gymImage.shrink(file, { maxSide: 2000, quality: 0.85, maxBytes: 2 * 1024 * 1024, steps: [[2000, 0.7], [1600, 0.7], [1200, 0.6]] });
            } catch {
                window.gymToast(t("Couldn't read that picture. Try another photo."));
                return;
            }

            const form = host.querySelector("form");
            const data = new FormData();
            data.append("photo", shot.blob, "label.jpg");
            data.set("width", String(shot.width));
            data.set("height", String(shot.height));
            data.set("date", form ? form.elements.date.value : slot.dataset.date);
            data.set("meal", form ? form.elements.meal.value : slot.dataset.meal);
            const typedName = form && form.elements.name ? form.elements.name.value.trim() : "";
            if (typedName) {
                data.set("name", typedName);
            }
            const barcodeInput = form && form.querySelector("[data-barcode-input]");
            const knownBarcode = form && form.elements.barcode ? form.elements.barcode.value : "";
            const found = knownBarcode ? null : await barcodeIn(shot.blob);
            data.set("barcode", knownBarcode || found || "");
            data.set("barcodeField", String(slot ? true : Boolean(barcodeInput)));

            const response = await fetch("/food/label", {
                method: "POST",
                body: data,
                headers: { "RequestVerificationToken": window.gymToken(), "HX-Request": "true" },
                signal: controller.signal,
            });
            if (pending !== controller) {
                return;
            }
            if (response.status === 429) {
                window.gymToast(t("Too many photos for now. Try again in a few minutes."));
                return;
            }
            const html = await response.text();
            const doc = new DOMParser().parseFromString(html, "text/html");
            const fresh = doc.querySelector("[data-label-form]");
            if (fresh) {
                if (slot) {
                    slot.replaceChildren(fresh);
                } else {
                    formOf(input).replaceWith(fresh);
                }
                toggleShare(fresh.querySelector("form"));
                fresh.querySelector(".banner")?.scrollIntoView({ block: "start", behavior: "smooth" });
            } else {
                window.gymToast(doc.body.textContent.trim() || t("Couldn't read the label. Enter the values by hand."));
            }
        } catch (error) {
            if (error.name !== "AbortError") {
                window.gymToast(t("Couldn't read the label. Enter the values by hand."));
            }
        } finally {
            busy.classList.remove("is-busy");
            if (pending === controller) {
                pending = null;
            }
        }
    }

    function toggleShare(form) {
        const input = form.querySelector("[data-barcode-input]");
        if (!input) {
            return;
        }
        const show = input.value.replace(/\D/g, "").length >= 8;
        form.querySelectorAll("[data-share]").forEach((el) => { el.hidden = !show; });
    }

    function onChange(event) {
        if (event.target.matches("[data-label-photo]")) {
            const slot = event.target.closest("[data-label-start]") ? document.querySelector("[data-label-slot]") : null;
            read(event.target, slot);
        }
    }

    function onInput(event) {
        if (event.target.matches("[data-barcode-input]")) {
            toggleShare(event.target.closest("form"));
        }
    }

    root.addEventListener("change", onChange);
    root.addEventListener("input", onInput);
    window.addEventListener("pagehide", () => pending?.abort());
    return () => {
        pending?.abort();
        root.removeEventListener("change", onChange);
        root.removeEventListener("input", onInput);
    };
});
```

`t` is the global translate function from `i18n.js`, `window.gymToast` and `window.gymToken()` are the toast and anti-forgery helpers `feedback.js` uses, and `window.gymModule(name, fn)` runs `fn` for each element with `data-module="name"` (see `site.js:480`). Put `data-module="label"` on a wrapper around the tab content in `Add.cshtml` and around the not-found section of `Barcode.cshtml`; a 400 from `FeatureExceptionFilter` has the message as its body (as `feedback.js:363` relies on), which the `else` branch shows. Disable the photo buttons while `navigator.onLine` is false and on `online`/`offline` events, with the "Needs a connection" title.

Add the extra strings: "Couldn't read that picture. Try another photo." ("Снимката не можа да се отвори. Опитай с друга."), "Too many photos for now. Try again in a few minutes." ("Твърде много снимки засега. Опитай пак след няколко минути."). JS strings go wherever `feedback.js`'s strings are catalogued (check `i18n.js` and how `CatalogueTests` finds `t("…")` calls).

- [ ] **Step 3: E2E stubs.** In `AiStubs.cs`:

```csharp
public sealed class StubLabelReader : ILabelReader
{
    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    private readonly IHttpContextAccessor http;

    public StubLabelReader(IHttpContextAccessor http) => this.http = http;

    // A form posted with the name "slow" answers late, so the second-photo test can overtake it.
    public async Task<LabelReadResult> ReadAsync(byte[] image, string contentType, CancellationToken ct)
    {
        if (http.HttpContext?.Request.Form["name"] == "slow")
        {
            await Task.Delay(1500, ct);
        }

        return new(new LabelReading(true, "Айрян", null, LabelBasis.Per100ml, null, 38, 159, 1.8m, 2.6m, 2.2m, 1.4m, null), "{}", null, "test/label");
    }
}
```

Register it on the AI app in `E2EFixture` next to the other stubs, plus `s.AddHttpContextAccessor()`. The slow path is chosen by the posted name, not by the bytes, so the test controls it.

Test photos: commit `Fixtures/label.jpg` (copy of Task 6's `sample-label.jpg`), `Fixtures/label-with-barcode.jpg` (the sample with EAN-13 `5901234123457` drawn under the table as SVG bars from the EAN-13 L/G/R pattern table, written by `tools/labels/make-sample.mjs` as a second output), and `Fixtures/not-an-image.jpg` (text bytes with a .jpg name).

- [ ] **Step 4: E2E tests** in `LabelPhotoTests.cs`, on the AI app at 390 px, Chromium and WebKit, English and Bulgarian where marked:
  - `ManualTabFillsFromPhoto` (en, bg): set the file input to `label.jpg`; energy becomes `38`, protein `1.8`; the banner and the "Per 100 ml, used as per 100 g." note show; Save and add logs the food; the Food day shows it.
  - `BarcodeMissFillsAndSharesByDefault`: scan or type an unknown barcode, photo, save; a second user's scan of that barcode finds it with "Added by another user".
  - `ScanTabPhotographLabelOpensFilledForm`: tap "Photograph a label" with `label-with-barcode.jpg`; the slot shows the form with the barcode filled and the share block visible.
  - `UndecodablePhotoLeavesFormAlone` (Review Focus 1): `not-an-image.jpg`; a toast shows, "Reading the label…" disappears, typed values are unchanged.
  - `SecondPhotoWins` (Review Focus 2): type "slow" as the name, pick a photo, then immediately clear the name and pick another; after both settle the form shows one filled result whose name field is empty-then-filled from the second answer ("Айрян"), the first request was aborted (check with `page.RequestFailed`), and no error toast appears.
  - `TypedNameIsKept` (Review Focus 3): type "Моят айрян", then photo; name stays "Моят айрян", energy becomes 38.
  - `OfflineDisablesPhoto`: `context.SetOfflineAsync(true)`; the photo button is disabled with "Needs a connection".
  - `ShareToggleFollowsBarcodeField`: on the Manual tab the share block is hidden, appears after 13 digits are typed, hides when cleared.

- [ ] **Step 5: Run.** `dotnet test tests/Plate.E2E.Tests --filter "LabelPhotoTests|FeedbackExtras|AiPathTests"` (feedback screenshots still work with the moved `shrink`), then all Core and Web.

- [ ] **Step 6: Commit.** "Client: label photo resize, barcode from the photo and form swap"

---

### Task 10: Retention, account deletion and export

**Files:**
- Modify: `src/Plate.Web/Features/Undo/SoftDeletePurge.cs:12-15, 56-75`
- Modify: `src/Plate.Web/Features/Admin/UserDeleter.cs:20-35`
- Modify: `src/Plate.Web/Features/Export/DataExport.cs:16-28`
- Test: `tests/Plate.Web.Tests/DeletedRowsTests.cs` (or wherever `PurgeFeedbackAsync` is tested), `AccountCliTests.cs`, `ExportTests.cs`

**Interfaces:**
- Consumes: entities (Task 3).
- Produces: `SoftDeletePurge.KeepLabelPhotos = TimeSpan.FromDays(30)`, `KeepLabelScans = TimeSpan.FromDays(180)`; `label_scans.csv` in the export.

- [ ] **Step 1: Failing tests.**
  - `PurgeDeletesOldPhotosButKeepsScan`: a scan 31 days old loses its photo and keeps its row; one 29 days old keeps both.
  - `PurgeDeletesScansAfter180Days`: a 181-day-old scan is gone (its photo with it).
  - `DeleteUserRemovesScansAndPhotos`: after `delete-user`, no `LabelScan` or `LabelScanPhoto` row has the user id; a shared food the user added stays, with `AddedByUserId` null.
  - `ExportIncludesLabelScansWithoutPhotos`: the zip has `label_scans.csv` with created, model, error, edited and the values JSON, and no image bytes.

- [ ] **Step 2: Run and see them fail.**

- [ ] **Step 3: Implement.** In `PurgeFeedbackAsync`:

```csharp
        count += await db.LabelScanPhotos.IgnoreQueryFilters().Where(p => p.LabelScan.CreatedAt < now - KeepLabelPhotos).ExecuteDeleteAsync(ct);
        count += await db.LabelScans.IgnoreQueryFilters().Where(s => s.CreatedAt < now - KeepLabelScans).ExecuteDeleteAsync(ct);
```

`UserDeleter`: the scans cascade from the user row (Task 3's FK) and `AddedByUserId` is `SetNull`, so no code change should be needed; the test proves it. If the cascade from `LabelScan` to `FoodItem` interferes, delete the user's scans explicitly before `users.DeleteAsync`. Update the doc comment of `delete-user` usage if it lists what goes.

`DataExport`: `["label_scans.csv"] = await LabelScansAsync(ct)`, written like `FeedbackAsync`.

- [ ] **Step 4: Run.** The three test classes, then all Core and Web.

- [ ] **Step 5: Commit.** "Label scan retention, account deletion and export"

---

### Task 11: Admin Labels page, label accuracy and CLI

**Files:**
- Create: `src/Plate.Web/Controllers/AdminLabelsController.cs`, `Views/AdminLabels/List.cshtml`, `Views/AdminLabels/Scan.cshtml`
- Create: `src/Plate.Web/Features/Feedback/LabelScanReport.cs` (queries shared by admin and CLI)
- Modify: `src/Plate.Web/Features/Feedback/ModelAccuracyReport.cs:17` (`ForAsync(AiTask.Label)`), `Views/AdminAi/Model.cshtml` (label wording)
- Modify: `src/Plate.Web/Cli/FeedbackCommand.cs` (`labels`, `label`, `label-photo`, digest counts)
- Modify: `src/Plate.Web/Features/Admin/AdminAuditLog.cs` (`AuditActions.LabelPhotoDownload`)
- Modify: admin nav partial (add Labels), `README.md`, `docs/feedback-triage.md` (if the project skill lists commands)
- Test: new `tests/Plate.Web.Tests/AdminLabelsTests.cs`, `FeedbackTests.cs` (CLI)

**Interfaces:**
- Consumes: entities (Task 3), `ModelAccuracy.Summarize` and `RunOutcome` (Core).
- Produces:
  - `LabelScanReport.ListAsync(DateTimeOffset since, bool editedOnly, bool failedOnly, CancellationToken ct) : Task<List<LabelScanRow>>` with `record LabelScanRow(int Id, DateTimeOffset CreatedAt, string UserName, string Model, int LatencyMs, string? Error, bool Saved, bool Edited, bool HasPhoto)`
  - `LabelScanReport.GetAsync(int id, CancellationToken ct) : Task<LabelScanDetail?>`, `PhotoAsync(int id, CancellationToken ct) : Task<LabelScanPhoto?>`
  - Admin routes `/admin/labels`, `/admin/labels/{id}`, `/admin/labels/{id}/photo`.

- [ ] **Step 1: Failing tests.**
  - `AdminSeesLabelsAcrossAccounts`: two users' scans appear in `/admin/labels` for an unlocked Dev (reuse the admin unlock helper from `AdminTests`), newest first; `?edited=true` filters.
  - `ScanPageShowsPhotoAndValues`: `/admin/labels/{id}` has an `<img src="/admin/labels/{id}/photo">`, the model's reading and the saved values; the photo endpoint returns `image/jpeg` with `Cache-Control: no-store`.
  - `PhotoGoneAfterPurgeShowsNote`: after the photo is purged the page says "Photo deleted after 30 days." and the photo route 404s.
  - `NonDevGets404`: a normal account gets 404 on all three routes.
  - `LabelAccuracyOnModelPage`: 20 scans for one model with 5 edited and 2 failed show 25% edited (of saved) and 10% failures on `/admin/ai/model/label`.
  - CLI: `feedback labels --since 30d --edited` lists edited scans; `feedback label <id>` prints reading, values and saved; `feedback label-photo <id>` writes the bytes to stdout and an `admin audit` row `label-photo-download`; `feedback digest` has a "Label reads: N (F failed, E edited)" line.

- [ ] **Step 2: Run and see them fail.**

- [ ] **Step 3: Implement.** Model the controller and views on `AdminFeedbackController`'s Parses and Parse pages (same layout, filters, English only, `IgnoreQueryFilters()` in `LabelScanReport` because admin reads across accounts like parse runs). The photo action audits `LabelPhotoDownload` with the scan id as target. `ModelAccuracyReport.ForAsync(AiTask.Label)` maps scans from the last 90 days to `new RunOutcome(s.Model, Confirmed: s.FoodItemId != null, Corrected: s.Edited, RatedDown: s.Error != null)`; in `Model.cshtml`, when the task is Label, label the columns "Reads", "Edited (of saved)" and "Failed" instead of thumbs-down. CLI subcommands follow the `parses` and `attachment` patterns in `FeedbackCommand.cs`, with `--json` support.

- [ ] **Step 4: Docs.** README: a short "Label photos" section (what it does, the model setting, `ai test label`, retention). Update the `/feedback` skill's command list with `labels`, `label` and `label-photo` if it lists commands. `.env.example` needs nothing new (the model is a database setting); confirm `RateLimits__AiLabelPerWindow` is documented if the other limits are.

- [ ] **Step 5: Run.** `AdminLabelsTests`, `AdminTests`, `FeedbackTests`, then all Core and Web, then the full E2E suite (end of the workstream).

- [ ] **Step 6: Commit.** "Admin Labels page, label model accuracy and feedback CLI for label scans"

---

## Verification

1. `dotnet build` with no warnings added; `dotnet test` (Core, Web, E2E) all green. Report the real counts.
2. Locally with a real key (`OPENROUTER_API_KEY` in the shell, dev db up): `dotnet run --project src/Plate.Web`, then `dotnet src/Plate.Web/bin/Debug/net10.0/Plate.Web.dll ai test label` reads the sample at 250 kcal.
3. Chrome at 390 px, English and Bulgarian: the composer looks centred on Food and in the setup chat; a real label photo fills the form on all three entry points.
4. Merge `label-photos` into `main` after the full E2E run, take the pre-deploy dump as in previous phases, deploy to wizard (`docker compose -f docker/docker-compose.yml up -d --build`), check the migration applied and the backfill row count in the logs.
5. On the iPhone (Phase 13 Done-when): camera and library photos of a real Bulgarian label after a barcode miss; the other account scans it and sees "Added by another user"; an unticked product stays private; a per-serving label converts; a non-label photo shows the message; `ai test label` on wizard.
6. Add a What's new entry in English and Bulgarian with `feedback changelog add` after deploying.
