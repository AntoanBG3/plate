# Phase 16: Log it again faster — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Offer what the account did last time: the last amount on barcode and search results, "Add and scan another", Recent ordered by meal, and the food last confirmed for typed words, with no AI call.

**Architecture:** Three pure functions in Core (`LastAmount`, `RecentOrder`, `UsualFoods.Key`, SPEC 5.10) and one Web service, `RepeatLoggingService`, that reads the account's `FoodLog` and a new `UsualFood` table through the owner query filters. The barcode and search screens ask it for last amounts; `FoodLogService.RecentsAsync` orders through `RecentOrder`; `FoodMatcher` asks it for usual foods before the ranking and for last amounts after matching; `ParseRunService` writes usual foods after a confirm. The parser gains `AmountGiven` so a typed amount is never replaced.

**Tech Stack:** .NET 10, ASP.NET Core MVC + Razor, EF Core + Npgsql (PostgreSQL 18), xUnit + FluentAssertions + Testcontainers, Playwright (E2E project). No JavaScript changes.

**Spec:** `SPEC.md` sections 4 (UsualFood), 5.10, 6.1 (AmountGiven, resolving steps 1 and 4, usual foods), 7 Food (barcode, search, Recent, confirmation list), 11 (Resolved, `parses`, retention) and 13 (Phase 16). Background and evidence: `docs/ideas/2026-10-11-brainstorm.md`, idea 1.

## Decisions already made (Antoan, 2026-10-11)

- Friends log mostly by barcode, the father rarely logs, Antoan types text. So the barcode and search parts ship in the same phase as the typed-words part.
- "Add and scan another" lands on the Scan tab and waits for a tap on "Scan a barcode". It does not start the camera by itself.
- The last amount is one value per food for the whole account, used by every path; a usual food stores only which food the words mean.
- A typed amount is never replaced. "A banana" or a bare name is not an amount.

## Global Constraints

- `Plate.Core` has no references to EF Core, ASP.NET Core or HttpClient. `LastAmount`, `RecentOrder` and `UsualFoods` are pure and take plain records.
- Every user-owned query is scoped to the current user. `UsualFoods` and `FoodLog` reads rely on their owner query filters; a usual food is used only when `db.FoodItems` (own or shared) returns its food.
- No new NuGet packages or front-end libraries. No JS or CSS changes are planned; if a task does change a file under `wwwroot`, bump `CACHE` in `wwwroot/sw.js` from `gym-static-v25` to `gym-static-v26` once.
- Mobile-first at 390 and 320 px; every tap target at least 44 px (the "Add and scan another" button).
- Windows: last amount 90 days by `CreatedAt` (`LastAmount.Window`); Recent 30 days by `Date` and 20 foods (`RecentOrder.Days`, `RecentOrder.Count`); usual foods removed after 180 days unused (`SoftDeletePurge.KeepUsualFoods`); `UsualFood.Phrase` at most 100 characters (`UsualFoods.MaxLength`).
- Exact English strings: "Last time", "Add and scan another", "Added: {0}", "Your usual", "Your usual amount, {0} g". Bulgarian in `Resources/strings.bg.json`, informal "ти": "Последно", "Добави и сканирай още", "Добавено: {0}", "Обичайно", "Обичайното ти количество, {0} г". After adding strings run `GYM_WRITE_KEYS=1 dotnet test tests/Plate.Web.Tests --filter CatalogueTests`; `CatalogueTests` and `BulgarianCatalogueTests` stay green.
- Numbers render with a dot in both languages (SPEC 8); grams in notes use `ToString("0.#", CultureInfo.InvariantCulture)` like the existing serving note.
- Recording never gets in the way of logging food: a failure to write a usual food is logged and swallowed.
- Test cadence: after each task run its new or affected E2E tests (filtered) plus all Core and Web tests (`dotnet test tests/Plate.Core.Tests && dotnet test tests/Plate.Web.Tests`). Full E2E suite in Task 8 and before merging. A failing run stops the task; never pipe test output through `grep` without keeping the exit code.
- One commit per task, message ending with the session's attribution lines. Branch `log-again-faster` (exists, holds the spec); do not push.

## Review Focus

1. **A typed amount is never replaced.** "300г извара" after a usual 200 g stays 300 g; "2 eggs" stays two servings. Pinned in Task 6 (`The_fake_parser_marks_typed_amounts_and_counts`) and Task 7 (`A_typed_amount_is_kept`).
2. **A usual food the account can't see is ignored.** A row pointing at another account's private food (or a food deleted since) must fall through to the ranking, never show that food. Pinned in Task 7 (`A_usual_food_pointing_at_a_food_this_account_cannot_see_is_ignored`, `Deleting_the_food_removes_its_usual_food`).
3. **The same words twice, or a double submit, must not lose food.** Two items with one key in one parse, or the confirm form sent twice, write one row and never throw out of the confirm. Pinned in Task 7 (`The_same_words_twice_in_one_parse_write_one_row`, `Remembering_twice_keeps_one_row`).
4. **Another account's history never leaks.** Last amounts and the Recent order use only the account's own entries. Pinned in Task 3 (`Last_amounts_come_from_this_accounts_entries_only`) and Task 5 (`Recent_ignores_other_accounts`).
5. **An unnamed product logged with "Add and scan another" keeps its name,** and a Quick entry (0 g) never prefills 0. Pinned in Task 4 (`Add_and_scan_another_names_an_unnamed_product`) and Task 1 (`Quick_entries_and_old_entries_are_ignored`).

---

### Task 1: Repeat logging in Core (SPEC 5.10)

**Files:**
- Create: `src/Plate.Core/Nutrition/RepeatLogging.cs`
- Test: `tests/Plate.Core.Tests/RepeatLoggingTests.cs`

**Interfaces:**
- Consumes: `Plate.Core.Domain.Meal`.
- Produces:
  - `record AmountUse(decimal Grams, DateTimeOffset CreatedAt)`; `static class LastAmount { static readonly TimeSpan Window; static decimal? Pick(IEnumerable<AmountUse> uses, DateTimeOffset now) }`
  - `record RecentUse(int EntryId, int? FoodItemId, string Description, Meal Meal, DateOnly Date, DateTimeOffset CreatedAt, decimal Grams)`; `record RecentGroup(int LastEntryId, int Times)`; `static class RecentOrder { const int Days = 30; const int Count = 20; static IReadOnlyList<RecentGroup> For(IEnumerable<RecentUse> uses, Meal meal, DateOnly today) }`
  - `static partial class UsualFoods { const int MaxLength = 100; static string? Key(string? typed) }`

- [ ] **Step 1: Write the failing tests.** Create `tests/Plate.Core.Tests/RepeatLoggingTests.cs`:

```csharp
using FluentAssertions;
using Plate.Core.Domain;
using Plate.Core.Nutrition;

namespace Plate.Core.Tests;

public class RepeatLoggingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 11);

    [Fact]
    public void The_latest_entry_wins_over_a_more_frequent_older_one()
    {
        LastAmount.Pick([
            new AmountUse(200, Now.AddDays(-10)),
            new AmountUse(200, Now.AddDays(-9)),
            new AmountUse(200, Now.AddDays(-8)),
            new AmountUse(250, Now.AddDays(-1)),
        ], Now).Should().Be(250);
    }

    [Fact]
    public void Quick_entries_and_old_entries_are_ignored()
    {
        LastAmount.Pick([new AmountUse(0, Now.AddHours(-1)), new AmountUse(150, Now.AddDays(-2))], Now).Should().Be(150);
        LastAmount.Pick([new AmountUse(150, Now.AddDays(-91))], Now).Should().BeNull();
        LastAmount.Pick([new AmountUse(150, Now.AddDays(-90))], Now).Should().Be(150);
        LastAmount.Pick([], Now).Should().BeNull();
    }

    private static int _id;

    private static IEnumerable<RecentUse> Uses(int? food, string name, Meal meal, int times, int daysAgo = 1) =>
        Enumerable.Range(0, times).Select(i =>
            new RecentUse(++_id, food, name, meal, Today.AddDays(-daysAgo), Now.AddDays(-daysAgo).AddMinutes(i), 100));

    [Fact]
    public void Foods_eaten_at_the_meal_come_first()
    {
        var uses = Uses(1, "Oats", Meal.Breakfast, 5).Concat(Uses(1, "Oats", Meal.Dinner, 1))
            .Concat(Uses(2, "Rice", Meal.Dinner, 3)).ToList();
        var oats = uses.Where(u => u.FoodItemId == 1).MaxBy(u => u.CreatedAt)!.EntryId; // the latest breakfast entry

        var breakfast = RecentOrder.For(uses, Meal.Breakfast, Today);
        var dinner = RecentOrder.For(uses, Meal.Dinner, Today);

        breakfast[0].Should().Be(new RecentGroup(oats, 6));
        dinner.Select(g => g.Times).Should().Equal(3, 6);
    }

    [Fact]
    public void Ties_fall_to_the_total_then_the_latest_entry()
    {
        var uses = Uses(1, "Apple", Meal.Lunch, 2, daysAgo: 5)
            .Concat(Uses(2, "Pear", Meal.Lunch, 2, daysAgo: 1))
            .Concat(Uses(3, "Plum", Meal.Lunch, 2, daysAgo: 3)).Concat(Uses(3, "Plum", Meal.Dinner, 1, daysAgo: 3)).ToList();

        var order = RecentOrder.For(uses, Meal.Breakfast, Today).Select(g => uses.Single(u => u.EntryId == g.LastEntryId).Description);

        order.Should().Equal("Plum", "Pear", "Apple");
    }

    [Fact]
    public void Recent_keeps_30_days_skips_quick_entries_and_shows_20()
    {
        var uses = Uses(1, "Old", Meal.Lunch, 1, daysAgo: 31)
            .Concat(Uses(2, "Edge", Meal.Lunch, 1, daysAgo: 30))
            .Append(new RecentUse(++_id, null, "Wrap", Meal.Lunch, Today, Now, 0))
            .Concat(Enumerable.Range(10, 25).SelectMany(n => Uses(n, $"Food {n}", Meal.Lunch, 1)))
            .ToList();

        var order = RecentOrder.For(uses, Meal.Lunch, Today);

        order.Should().HaveCount(20);
        var names = RecentOrder.For(uses.Where(u => u.Description is "Old" or "Edge" or "Wrap"), Meal.Lunch, Today)
            .Select(g => uses.Single(u => u.EntryId == g.LastEntryId).Description);
        names.Should().Equal("Edge");
    }

    [Fact]
    public void Entries_group_by_food_and_name_ignoring_case()
    {
        var uses = Uses(1, "Skyr", Meal.Lunch, 1).Concat(Uses(1, "skyr", Meal.Lunch, 1)).Concat(Uses(1, "Skyr vanilla", Meal.Lunch, 1)).ToList();

        RecentOrder.For(uses, Meal.Lunch, Today).Select(g => g.Times).Should().BeEquivalentTo([2, 1]);
    }

    [Theory]
    [InlineData("200г извара", "извара")]
    [InlineData("Извара", "извара")]
    [InlineData("извара  ", "извара")]
    [InlineData("2 бр. банани", "банани")]
    [InlineData("½ avocado", "avocado")]
    [InlineData("1/2 avocado", "avocado")]
    [InlineData("1,5 кг ориз", "ориз")]
    [InlineData("извара 200 g", "извара")]
    [InlineData("200g, cottage cheese", "cottage cheese")]
    [InlineData("мляко 2%", "мляко 2%")]
    [InlineData("7days croissant", "7days croissant")]
    [InlineData("pão  de   queijo", "pão de queijo")]
    public void Typed_names_give_one_key_without_their_amount(string typed, string key) =>
        UsualFoods.Key(typed).Should().Be(key);

    [Theory]
    [InlineData("200 g")]
    [InlineData("  ")]
    [InlineData(null)]
    [InlineData("...")]
    public void An_amount_alone_gives_no_key(string? typed) =>
        UsualFoods.Key(typed).Should().BeNull();

    [Fact]
    public void A_key_is_at_most_100_characters() =>
        UsualFoods.Key(new string('a', 150))!.Length.Should().Be(UsualFoods.MaxLength);
}
```

- [ ] **Step 2: Run them to see them fail.** `dotnet test tests/Plate.Core.Tests --filter RepeatLoggingTests` → build error, `LastAmount` not defined.

- [ ] **Step 3: Implement.** Create `src/Plate.Core/Nutrition/RepeatLogging.cs`:

```csharp
using System.Text.RegularExpressions;
using Plate.Core.Domain;

namespace Plate.Core.Nutrition;

/// <summary>One past entry of a food: how much, and when it was logged (SPEC 5.10).</summary>
public record AmountUse(decimal Grams, DateTimeOffset CreatedAt);

/// <summary>The amount this account logged last time for a food (SPEC 5.10).</summary>
public static class LastAmount
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(90);

    /// <summary>
    /// The grams of the most recent entry in the window, or null. Quick entries (0 g) don't count. The most recent wins
    /// over the most frequent, since an amount changes when a pack size or a habit does.
    /// </summary>
    public static decimal? Pick(IEnumerable<AmountUse> uses, DateTimeOffset now) =>
        uses.Where(u => u.Grams > 0 && u.CreatedAt >= now - Window)
            .OrderByDescending(u => u.CreatedAt)
            .Select(u => (decimal?)u.Grams)
            .FirstOrDefault();
}

/// <summary>One food log entry as the Recent tab sees it.</summary>
public record RecentUse(int EntryId, int? FoodItemId, string Description, Meal Meal, DateOnly Date, DateTimeOffset CreatedAt, decimal Grams);

/// <summary>One Recent row: the entry it is drawn from (the latest of its group) and how often it was logged.</summary>
public record RecentGroup(int LastEntryId, int Times);

/// <summary>The Recent tab's foods for a meal (SPEC 5.10): what the account eats at that meal first.</summary>
public static class RecentOrder
{
    public const int Days = 30;
    public const int Count = 20;

    public static IReadOnlyList<RecentGroup> For(IEnumerable<RecentUse> uses, Meal meal, DateOnly today)
    {
        var since = today.AddDays(-Days);
        return uses
            .Where(u => u.Date >= since && u.Grams > 0)
            .GroupBy(u => (u.FoodItemId, Key: u.Description.ToLowerInvariant()))
            .Select(g =>
            {
                var last = g.OrderByDescending(u => u.Date).ThenByDescending(u => u.CreatedAt).First();
                return (Last: last, AtMeal: g.Count(u => u.Meal == meal), Times: g.Count());
            })
            .OrderByDescending(x => x.AtMeal)
            .ThenByDescending(x => x.Times)
            .ThenByDescending(x => x.Last.Date)
            .ThenByDescending(x => x.Last.CreatedAt)
            .ThenBy(x => x.Last.Description, StringComparer.Ordinal)
            .Take(Count)
            .Select(x => new RecentGroup(x.Last.EntryId, x.Times))
            .ToList();
    }
}

/// <summary>The key a usual food is stored under: what the user typed for a food, without its amount (SPEC 5.10).</summary>
public static partial class UsualFoods
{
    public const int MaxLength = 100;

    private static readonly HashSet<string> Units = ["g", "гр", "г", "kg", "кг", "ml", "мл", "l", "л", "бр"];

    public static string? Key(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
        {
            return null;
        }

        var tokens = typed.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(TrimEdges)
            .Where(t => t.Length > 0)
            .ToList();

        // "200 g извара", "2 бр. банани": an amount, then maybe its unit.
        while (tokens.Count > 0 && IsAmount(tokens[0]))
        {
            var number = IsNumber(tokens[0]);
            tokens.RemoveAt(0);
            if (number && tokens.Count > 0 && Units.Contains(tokens[0]))
            {
                tokens.RemoveAt(0);
            }
        }

        // "извара 200 g", "извара 200г".
        while (tokens.Count > 0)
        {
            if (IsAmount(tokens[^1]))
            {
                tokens.RemoveAt(tokens.Count - 1);
            }
            else if (tokens.Count > 1 && Units.Contains(tokens[^1]) && IsNumber(tokens[^2]))
            {
                tokens.RemoveRange(tokens.Count - 2, 2);
            }
            else
            {
                break;
            }
        }

        var key = string.Join(' ', tokens);
        return key.Length == 0 ? null : key.Length > MaxLength ? key[..MaxLength] : key;
    }

    /// <summary>Punctuation at a word's edges goes; inside a word ("7-up", "1/2") and a percent sign ("2%") stay.</summary>
    private static string TrimEdges(string token)
    {
        int start = 0, end = token.Length;
        while (start < end && IsEdge(token[start]))
        {
            start++;
        }

        while (end > start && IsEdge(token[end - 1]))
        {
            end--;
        }

        return token[start..end];
    }

    private static bool IsEdge(char c) => c != '%' && (char.IsPunctuation(c) || char.IsSymbol(c));

    private static bool IsNumber(string token) => Number().IsMatch(token);

    private static bool IsAmount(string token) => IsNumber(token) || NumberWithUnit().IsMatch(token);

    [GeneratedRegex(@"^(\d+([.,]\d+)?|\d+/\d+|[½¼¾⅓⅔])$")]
    private static partial Regex Number();

    [GeneratedRegex(@"^(\d+([.,]\d+)?|\d+/\d+)(g|гр|г|kg|кг|ml|мл|l|л|бр)$")]
    private static partial Regex NumberWithUnit();
}
```

- [ ] **Step 4: Run the tests.** `dotnet test tests/Plate.Core.Tests --filter RepeatLoggingTests` → all pass. Then `dotnet test tests/Plate.Core.Tests` → all pass.

- [ ] **Step 5: Commit.** `git add src/Plate.Core/Nutrition/RepeatLogging.cs tests/Plate.Core.Tests/RepeatLoggingTests.cs && git commit -m "Add repeat logging in Core: last amount, Recent order, usual food key"` (with attribution lines).

---

### Task 2: Store usual foods

**Files:**
- Modify: `src/Plate.Core/Domain/Nutrition.cs` (add `UsualFood` after `FoodLogEntry`)
- Modify: `src/Plate.Web/Data/AppDbContext.cs` (DbSet, entity config, FoodLog index)
- Create: `src/Plate.Web/Data/Migrations/<timestamp>_UsualFoods.cs` (generated)
- Modify: `src/Plate.Web/Features/Undo/SoftDeletePurge.cs`
- Modify: `src/Plate.Web/Features/Export/DataExport.cs`
- Test: `tests/Plate.Web.Tests/RepeatLoggingTests.cs` (create), `tests/Plate.Web.Tests/ExportTests.cs`

**Interfaces:**
- Consumes: `UsualFoods.MaxLength` (Task 1).
- Produces: `class UsualFood { int Id; string UserId; string Phrase; int FoodItemId; DateTimeOffset LastUsedAt }`; `AppDbContext.UsualFoods`; `SoftDeletePurge.KeepUsualFoods`; export file `usual_foods.csv` with header `phrase,food_item_id,food,last_used_at_utc`.

- [ ] **Step 1: Write the failing tests.** Create `tests/Plate.Web.Tests/RepeatLoggingTests.cs`:

```csharp
using FluentAssertions;
using Plate.Core.Domain;
using Plate.Core.Nutrition;
using Plate.Web.Features.Food;
using Plate.Web.Features.Undo;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Tests;

/// <summary>SPEC 5.10 and 6.1: last amounts, the Recent order and usual foods, against a real database.</summary>
public class RepeatLoggingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static async Task<int> SharedFoodAsync(UserScope s, string query) =>
        (await s.Foods.SearchAsync(query, 1, 0.3, default)).Single().Id;

    [Fact]
    public async Task Usual_foods_unused_for_180_days_are_purged()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var food = await SharedFoodAsync(s, "banana");
        var now = DateTimeOffset.UtcNow;
        s.Db.UsualFoods.AddRange(
            new UsualFood { UserId = s.UserId, Phrase = "old", FoodItemId = food, LastUsedAt = now.AddDays(-181) },
            new UsualFood { UserId = s.UserId, Phrase = "new", FoodItemId = food, LastUsedAt = now.AddDays(-179) });
        await s.Db.SaveChangesAsync();

        await SoftDeletePurge.PurgeFeedbackAsync(s.Db, now, default);

        (await s.Db.UsualFoods.Select(u => u.Phrase).ToListAsync()).Should().Equal("new");
    }

    [Fact]
    public async Task A_phrase_is_unique_per_account_only()
    {
        await using var a = new UserScope(fixture, await fixture.CreateUserAsync());
        await using var b = new UserScope(fixture, await fixture.CreateUserAsync());
        var food = await SharedFoodAsync(a, "banana");
        a.Db.UsualFoods.Add(new UsualFood { UserId = a.UserId, Phrase = "банан", FoodItemId = food, LastUsedAt = DateTimeOffset.UtcNow });
        b.Db.UsualFoods.Add(new UsualFood { UserId = b.UserId, Phrase = "банан", FoodItemId = food, LastUsedAt = DateTimeOffset.UtcNow });
        await a.Db.SaveChangesAsync();
        await b.Db.SaveChangesAsync();

        (await a.Db.UsualFoods.CountAsync()).Should().Be(1, "each account sees only its own");
        a.Db.UsualFoods.Add(new UsualFood { UserId = a.UserId, Phrase = "банан", FoodItemId = food, LastUsedAt = DateTimeOffset.UtcNow });
        await FluentActions.Awaiting(() => a.Db.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }
}
```

In `tests/Plate.Web.Tests/ExportTests.cs`, add `"usual_foods.csv"` to the `BeEquivalentTo` list after `"label_scans.csv"` (around line 63), and add this test to the class:

```csharp
    [Fact]
    public async Task Usual_foods_are_exported()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var food = (await s.Foods.SearchAsync("banana", 1, 0.3, default)).Single();
        s.Db.UsualFoods.Add(new UsualFood { UserId = s.UserId, Phrase = "банан", FoodItemId = food.Id, LastUsedAt = DateTimeOffset.UtcNow });
        await s.Db.SaveChangesAsync();

        var files = Unzip(await new DataExport(s.Db).ZipAsync(default));

        files["usual_foods.csv"].Should().StartWith("phrase,food_item_id,food,last_used_at_utc\r\n")
            .And.Contain($"банан,{food.Id},");
    }
```

(`ExportTests` takes `PostgresFixture fixture` and has the `Unzip` helper; add `using Plate.Core.Domain;` if missing.)

- [ ] **Step 2: Run them to see them fail.** `dotnet test tests/Plate.Web.Tests --filter "RepeatLoggingTests|ExportTests"` → build error, `UsualFood` not defined.

- [ ] **Step 3: Add the entity.** In `src/Plate.Core/Domain/Nutrition.cs`, after the `FoodLogEntry` class:

```csharp
/// <summary>Which food this account means by some typed words (SPEC 4, 6.1). Written on confirm, read before the ranking.</summary>
public class UsualFood
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";

    /// <summary>The 5.10 key of a food's name as the user typed it; unique per user.</summary>
    public string Phrase { get; set; } = "";
    public int FoodItemId { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
}
```

- [ ] **Step 4: Configure it.** In `AppDbContext.cs`, next to `SavedMeals`:

```csharp
    public DbSet<UsualFood> UsualFoods => Set<UsualFood>();
```

After the `SavedMealItem` entity block (the `UsualFoods` property shadows the Core class here, hence the full name):

```csharp
        builder.Entity<UsualFood>(e =>
        {
            e.Property(x => x.Phrase).HasMaxLength(Plate.Core.Nutrition.UsualFoods.MaxLength);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<FoodItem>().WithMany().HasForeignKey(x => x.FoodItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.Phrase }).IsUnique();
            e.HasQueryFilter(x => x.UserId == CurrentUserId);
        });
```

In the `FoodLogEntry` block, after `e.HasIndex(x => new { x.UserId, x.Date });`:

```csharp
            e.HasIndex(x => new { x.UserId, x.FoodItemId, x.CreatedAt }); // last amounts (SPEC 5.10)
```

- [ ] **Step 5: Generate the migration.** `dotnet tool restore && dotnet ef migrations add UsualFoods --project src/Plate.Web --output-dir Data/Migrations`. Open the generated file and check it only creates `UsualFoods` (with the unique `(UserId, Phrase)` index and both cascading foreign keys) and the `FoodLog` index; `Down` drops both.

- [ ] **Step 6: Retention.** In `SoftDeletePurge.cs`, add beside the other `Keep…` fields:

```csharp
    public static readonly TimeSpan KeepUsualFoods = TimeSpan.FromDays(180);
```

and in `PurgeFeedbackAsync`, after the label scan line:

```csharp
        count += await db.UsualFoods.IgnoreQueryFilters().Where(u => u.LastUsedAt < now - KeepUsualFoods).ExecuteDeleteAsync(ct);
```

- [ ] **Step 7: Export.** In `DataExport.ZipAsync`, add `["usual_foods.csv"] = await UsualFoodsAsync(ct),` after `label_scans.csv`, and the method:

```csharp
    /// <summary>Which food the account means by words it types (SPEC 6.1).</summary>
    private async Task<Csv> UsualFoodsAsync(CancellationToken ct)
    {
        var csv = new Csv("phrase", "food_item_id", "food", "last_used_at_utc");
        var rows = await (from u in db.UsualFoods.AsNoTracking()
                          join f in db.FoodItems.AsNoTracking() on u.FoodItemId equals f.Id
                          orderby u.Phrase
                          select new { u.Phrase, u.FoodItemId, f.Name, u.LastUsedAt }).ToListAsync(ct);
        foreach (var r in rows)
        {
            csv.Row(r.Phrase, r.FoodItemId, r.Name, r.LastUsedAt);
        }

        return csv;
    }
```

Account deletion needs no change: the row cascades from the user (`UserDeleter` deletes the user last).

- [ ] **Step 8: Run the tests.** `dotnet test tests/Plate.Web.Tests --filter "RepeatLoggingTests|ExportTests|MigrationTests"` → pass. Then Core and Web in full.

- [ ] **Step 9: Commit.** "Store usual foods, with retention, export and a last-amount index".

---

### Task 3: The last amount on barcode and search results

**Files:**
- Create: `src/Plate.Web/Features/Food/RepeatLoggingService.cs`
- Modify: `src/Plate.Web/Program.cs` (register it beside `FoodMatcher`)
- Modify: `src/Plate.Web/Controllers/FoodController.cs` (constructor, `Add`, `Barcode`)
- Modify: `src/Plate.Web/ViewModels/ViewModels.cs` (`BarcodeViewModel`, `FoodAddViewModel`)
- Modify: `src/Plate.Web/Views/Food/Barcode.cshtml`, `src/Plate.Web/Views/Food/Add.cshtml` (search rows)
- Modify: `src/Plate.Web/Resources/strings.bg.json`, `strings.keys.json` (regenerated)
- Modify: `tests/Plate.Web.Tests/UserScope.cs`
- Test: `tests/Plate.Web.Tests/RepeatLoggingTests.cs`, `tests/Plate.E2E.Tests/RepeatLoggingFlowTests.cs` (create)

**Interfaces:**
- Consumes: `LastAmount` (Task 1), `AppDbContext.FoodLog`.
- Produces: `class RepeatLoggingService(AppDbContext db, ICurrentUser currentUser, TimeProvider time, ILogger<RepeatLoggingService> log)` with `Task<Dictionary<int, decimal>> LastAmountsAsync(IReadOnlyCollection<int> foodItemIds, CancellationToken ct)`; `UserScope.Repeat`; `BarcodeViewModel.LastGrams` (`decimal?`); `FoodAddViewModel.LastGrams` (`IReadOnlyDictionary<int, decimal>?`).

- [ ] **Step 1: Write the failing Web test.** Add to `RepeatLoggingTests`:

```csharp
    private static NewEntry Entry(int food, decimal grams, Meal meal = Meal.Lunch, string name = "Banana") =>
        new(Today, meal, name, grams, new Per100g(89, 1.1m, 23, 0.3m), food, EntrySource.Search);

    [Fact]
    public async Task Last_amounts_come_from_this_accounts_entries_only()
    {
        await using var a = new UserScope(fixture, await fixture.CreateUserAsync());
        await using var b = new UserScope(fixture, await fixture.CreateUserAsync());
        var food = await SharedFoodAsync(a, "banana");
        await b.FoodLog.AddAsync([Entry(food, 300)], default);

        (await a.Repeat.LastAmountsAsync([food], default)).Should().BeEmpty();

        await a.FoodLog.AddAsync([Entry(food, 120)], default);
        await a.FoodLog.AddAsync([Entry(food, 150)], default);
        (await a.Repeat.LastAmountsAsync([food], default)).Should().Equal(new Dictionary<int, decimal> { [food] = 150 });
    }
```

In `UserScope.cs`, after `Foods = …`, add `Repeat = new Plate.Web.Features.Food.RepeatLoggingService(Db, currentUser, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<Plate.Web.Features.Food.RepeatLoggingService>.Instance);` and the property `public Plate.Web.Features.Food.RepeatLoggingService Repeat { get; }`.

- [ ] **Step 2: Write the failing E2E test.** Create `tests/Plate.E2E.Tests/RepeatLoggingFlowTests.cs`:

```csharp
using FluentAssertions;
using Plate.Core.Domain;
using Plate.Core.Time;

namespace Plate.E2E.Tests;

/// <summary>Phase 16: log it again faster (SPEC 5.10, 7 Food).</summary>
public class RepeatLoggingFlowTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    private static string Today => UserClock.Today(DateTimeOffset.UtcNow, UserProfile.DefaultTimeZoneId).ToString("yyyy-MM-dd");

    private static string NewEan()
    {
        var digits = Enumerable.Range(0, 12).Select(i => i == 0 ? Random.Shared.Next(1, 10) : Random.Shared.Next(10)).ToArray();
        var sum = digits.Select((d, i) => i % 2 == 0 ? d : d * 3).Sum();
        return string.Concat(digits) + (10 - sum % 10) % 10;
    }

    /// <summary>A shared packaged product with a 40 g serving, as Open Food Facts would give it.</summary>
    private async Task<(string Code, string Name)> ProductAsync(bool unnamed = false)
    {
        var code = NewEan();
        var name = $"Oat bar {code[^5..]}";
        await using var db = app.Db.CreateContext(null);
        db.FoodItems.Add(new FoodItem { Name = name, Barcode = code, KcalPer100g = 400, ProteinPer100g = 10, CarbsPer100g = 60, FatPer100g = 12, DefaultServingGrams = 40, Source = FoodSource.OpenFoodFacts, Unnamed = unnamed });
        await db.SaveChangesAsync();
        return (code, name);
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_scanned_product_offers_the_amount_logged_last_time(string browser)
    {
        var (code, name) = await ProductAsync();
        await using var phone = await app.SignedInPhoneAsync(browser);

        await phone.GoAsync($"/food/barcode?date={Today}&meal=Lunch&code={code}");
        (await phone.Page.Locator("#grams").InputValueAsync()).Should().Be("40", "never logged: the serving");
        (await phone.Page.Locator("[data-last-time]").CountAsync()).Should().Be(0);
        await phone.Page.Locator("#grams").FillAsync("250");
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/log-item'] button[type=submit]").First.TapAsync());

        await phone.GoAsync($"/food/barcode?date={Today}&meal=Lunch&code={code}");
        (await phone.Page.Locator("#grams").InputValueAsync()).Should().Be("250");
        (await phone.Page.Locator("[data-last-time]").TextContentAsync())!.Trim().Should().Be("Last time");
        (await phone.Page.Locator("[data-totals]").TextContentAsync()).Should().Contain("1000 kcal");

        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=search&q={Uri.EscapeDataString(name)}");
        var row = phone.Page.Locator("#search-results form.portion", new() { HasText = name });
        (await row.Locator("input[name=grams]").InputValueAsync()).Should().Be("250");
        phone.ShouldHaveNoErrors();
    }
}
```

- [ ] **Step 3: Run them to see them fail.** `dotnet test tests/Plate.Web.Tests --filter RepeatLoggingTests` → build error (`Repeat`). `dotnet test tests/Plate.E2E.Tests --filter RepeatLoggingFlowTests` → fails on `#grams` being 40 after logging 250.

- [ ] **Step 4: Implement the service.** Create `src/Plate.Web/Features/Food/RepeatLoggingService.cs`:

```csharp
using Plate.Core.Nutrition;
using Plate.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Features.Food;

/// <summary>
/// What this account did last time (SPEC 5.10): the amount per food, and which food it means by typed words (6.1).
/// Reads go through the owner query filters, so another account's entries and usual foods are never seen.
/// </summary>
public class RepeatLoggingService(AppDbContext db, ICurrentUser currentUser, TimeProvider time, ILogger<RepeatLoggingService> log)
{
    /// <summary>The last amount for each food that has one; foods without one are left out.</summary>
    public async Task<Dictionary<int, decimal>> LastAmountsAsync(IReadOnlyCollection<int> foodItemIds, CancellationToken ct)
    {
        if (foodItemIds.Count == 0)
        {
            return [];
        }

        var now = time.GetUtcNow();
        var since = now - LastAmount.Window;
        var ids = foodItemIds.Distinct().ToList();
        var rows = await db.FoodLog
            .Where(e => e.FoodItemId != null && ids.Contains(e.FoodItemId.Value) && e.CreatedAt >= since && e.Grams > 0)
            .Select(e => new { Id = e.FoodItemId!.Value, e.Grams, e.CreatedAt })
            .ToListAsync(ct);

        var result = new Dictionary<int, decimal>();
        foreach (var food in rows.GroupBy(r => r.Id))
        {
            if (LastAmount.Pick(food.Select(r => new AmountUse(r.Grams, r.CreatedAt)), now) is { } grams)
            {
                result[food.Key] = grams;
            }
        }

        return result;
    }
}
```

(`currentUser` and `log` are used from Task 7 on; keep them in the constructor now so `UserScope` and DI don't change twice.)

Register it in `Program.cs` after `builder.Services.AddScoped<FoodMatcher>();`:

```csharp
builder.Services.AddScoped<RepeatLoggingService>();
```

- [ ] **Step 5: The barcode result.** In `ViewModels.cs`, add a parameter to `BarcodeViewModel`:

```csharp
/// <param name="LastGrams">This account's last amount for the product (SPEC 5.10), or null.</param>
public record BarcodeViewModel(DateOnly Date, Meal Meal, string Code, Plate.Web.Features.Food.BarcodeResult Result, bool AddedByAnother = false, bool LabelAvailable = false, decimal? LastGrams = null);
```

In `FoodController`, add `RepeatLoggingService repeat` to the constructor parameters (after `FoodItemService foods`), and in `Barcode` replace the `return View(new BarcodeViewModel(...))` with:

```csharp
        var result = await foods.LookupBarcodeAsync(digits, ct);
        decimal? last = null;
        if (result.Item is { } found && (await repeat.LastAmountsAsync([found.Id], ct)).TryGetValue(found.Id, out var grams))
        {
            last = grams;
        }

        return View(new BarcodeViewModel(date, meal, digits, result,
            AddedByAnother: result.Item is { } item && FoodItemService.AddedByAnother(item, currentUser.UserId),
            LabelAvailable: result.Item is null && await labels.IsAvailableAsync(ct),
            LastGrams: last));
```

In `Barcode.cshtml`, add to the top `@{ }` block after `var item = Model.Result.Item;`:

```csharp
    var amount = Model.LastGrams ?? item?.DefaultServingGrams ?? 100;
```

and replace the grams field row and the totals line (lines 40 to 42) with:

```cshtml
        <div class="list">
            <div class="field-row"><label for="grams">@L["How much?"]</label><input id="grams" class="num" type="text" name="grams" value="@amount.Kg()" min="1" max="3000" step="any" inputmode="decimal" pattern="[0-9]*[.,]?[0-9]*" autocomplete="off" required autofocus="@(!item.Unnamed)" /><span class="unit">@L["g"]</span></div>
        </div>
        @if (Model.LastGrams is not null)
        {
            <p class="section-footer" data-last-time>@L["Last time"]</p>
        }
        <p class="section-footer num" data-totals>@L["{0} kcal · {1} g protein", Math.Round(item.KcalPer100g * amount / 100), (item.ProteinPer100g * amount / 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)]</p>
```

- [ ] **Step 6: The search results.** Add `IReadOnlyDictionary<int, decimal>? LastGrams = null` as the last parameter of `FoodAddViewModel` (with a `<param>` line: "This account's last amount per listed food (SPEC 5.10)."). In `FoodController.Add`, after `var local = …`:

```csharp
        var lastGrams = local.Count > 0 ? await repeat.LastAmountsAsync(local.Select(f => f.Id).ToList(), ct) : null;
```

and pass `LastGrams: lastGrams` to the `FoodAddViewModel` it returns. In `Add.cshtml`'s search row, move the serving line above the grams label and make it use the last amount; the two lines become:

```cshtml
                                @{ var serving = Model.LastGrams is { } lastGrams && lastGrams.TryGetValue(f.Id, out var last) ? last : f.DefaultServingGrams ?? 100; }
                                <div class="portion-line">
                                    <label class="grams"><input type="text" name="grams" value="@serving.Kg()" min="1" max="3000" step="any" inputmode="decimal" pattern="[0-9]*[.,]?[0-9]*" autocomplete="off" aria-label="@L["Grams"]" required /><span class="footnote">@L["g"]</span></label>
```

(the `data-totals` span after it already uses `serving`).

- [ ] **Step 7: The string.** Add `"Last time": "Последно"` to `strings.bg.json` (a JSON object; keep its two-space indentation and add the entry before the closing brace), then `GYM_WRITE_KEYS=1 dotnet test tests/Plate.Web.Tests --filter CatalogueTests`.

- [ ] **Step 8: Run the tests.** `dotnet test tests/Plate.E2E.Tests --filter "RepeatLoggingFlowTests|FoodSearchTests|LabelPhotoTests|UntestedAreasTests"` → pass; then Core and Web in full.

- [ ] **Step 9: Commit.** "Offer the last amount on barcode and search results".

---

### Task 4: Add and scan another

**Files:**
- Modify: `src/Plate.Web/Controllers/FoodController.cs` (`LogItem`)
- Modify: `src/Plate.Web/Views/Food/Barcode.cshtml`
- Modify: `src/Plate.Web/Resources/strings.bg.json`, `strings.keys.json`
- Test: `tests/Plate.E2E.Tests/RepeatLoggingFlowTests.cs`

**Interfaces:**
- Consumes: the barcode result form from Task 3.
- Produces: `POST /food/log-item` accepts `next=scan` (only with `source=Barcode`) and redirects to `/food/add?date=…&meal=…&tab=barcode` with the notice "Added: {0}".

- [ ] **Step 1: Write the failing E2E tests.** Add to `RepeatLoggingFlowTests`:

```csharp
    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Add_and_scan_another_logs_three_products_into_one_meal(string browser)
    {
        var products = new[] { await ProductAsync(), await ProductAsync(), await ProductAsync() };
        await using var phone = await app.SignedInPhoneAsync(browser);

        for (var i = 0; i < 2; i++)
        {
            await phone.GoAsync($"/food/barcode?date={Today}&meal=Lunch&code={products[i].Code}");
            var another = phone.Page.Locator("button[name=next][value=scan]");
            var box = await Phone.BoxAsync(another);
            box.Height.Should().BeGreaterThanOrEqualTo(44);
            (box.X + box.Width).Should().BeLessThanOrEqualTo(390);
            await phone.NavigateByAsync(() => another.TapAsync());

            phone.Page.Url.Should().Contain("tab=barcode").And.Contain("meal=Lunch").And.Contain($"date={Today}");
            (await phone.Page.Locator(".toast, .banner").AllInnerTextsAsync()).Should().Contain(t => t.Contains($"Added: {products[i].Name}"));
            (await phone.Page.Locator("[data-start]").IsVisibleAsync()).Should().BeTrue("the next scan is one tap away");
        }

        await phone.GoAsync($"/food/barcode?date={Today}&meal=Lunch&code={products[2].Code}");
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/log-item'] button[type=submit]").First.TapAsync());

        var lunch = await phone.Page.Locator("#meal-lunch").InnerTextAsync();
        lunch.Should().Contain(products[0].Name).And.Contain(products[1].Name).And.Contain(products[2].Name);
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    public async Task Add_and_scan_another_names_an_unnamed_product(string browser)
    {
        var (code, _) = await ProductAsync(unnamed: true);
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync($"/food/barcode?date={Today}&meal=Lunch&code={code}");
        await phone.Page.Locator("#b-name").FillAsync("Granola square");

        await phone.NavigateByAsync(() => phone.Page.Locator("button[name=next][value=scan]").TapAsync());

        (await phone.Page.Locator(".toast, .banner").AllInnerTextsAsync()).Should().Contain(t => t.Contains("Added: Granola square"));
        await phone.GoAsync($"/food?date={Today}");
        (await phone.Page.Locator("#meal-lunch").InnerTextAsync()).Should().Contain("Granola square");
        phone.ShouldHaveNoErrors();
    }
```

- [ ] **Step 2: Run them to see them fail.** `dotnet test tests/Plate.E2E.Tests --filter "Add_and_scan_another"` → fails: no `button[name=next]`.

- [ ] **Step 3: The button.** In `Barcode.cshtml`, after the existing `<button type="submit" …>@L[AddTo[Model.Meal]]</button>` inside the form:

```cshtml
        <button type="submit" name="next" value="scan" class="btn btn-secondary btn-block" style="margin-top: var(--s-2)">@Ui.Icon("barcode")@L["Add and scan another"]</button>
```

- [ ] **Step 4: The redirect.** In `FoodController.LogItem`, add `string? next` as a parameter before `CancellationToken ct`, and replace the last two lines (`FocusMeal(meal); return Redirect(...)`) with:

```csharp
        if (next == "scan" && source == EntrySource.Barcode)
        {
            // The Scan tab for the same day and meal; the camera starts on the user's tap, as always (SPEC 7).
            TempData.Notice(new LocalText("Added: {0}", description));
            return Redirect($"/food/add?date={date:yyyy-MM-dd}&meal={meal}&tab=barcode");
        }

        FocusMeal(meal);
        return Redirect($"/food?date={date:yyyy-MM-dd}");
```

- [ ] **Step 5: The strings.** Add `"Add and scan another": "Добави и сканирай още"` and `"Added: {0}": "Добавено: {0}"` to `strings.bg.json` (note: `"Added {0}"` without the colon already exists with another meaning; keep both), then regenerate keys as in Task 3 Step 7.

- [ ] **Step 6: Run the tests.** `dotnet test tests/Plate.E2E.Tests --filter "RepeatLoggingFlowTests|UntestedAreasTests|LabelPhotoTests"` → pass; Core and Web in full.

- [ ] **Step 7: Commit.** "Add and scan another from the barcode result".

---

### Task 5: Recent by meal

**Files:**
- Modify: `src/Plate.Web/Features/Food/FoodLogService.cs` (`RecentsAsync`)
- Modify: `src/Plate.Web/Controllers/FoodController.cs` (`Add`)
- Modify: `tests/Plate.Web.Tests/NutritionTests.cs` (lines 182, 202), `tests/Plate.Web.Tests/QuickAddTests.cs` (line 22)
- Test: `tests/Plate.Web.Tests/RepeatLoggingTests.cs`, `tests/Plate.E2E.Tests/RepeatLoggingFlowTests.cs`

**Interfaces:**
- Consumes: `RecentOrder.For`, `RecentUse`, `RecentGroup` (Task 1).
- Produces: `Task<List<RecentFood>> RecentsAsync(DateOnly today, Meal meal, CancellationToken ct)` (replaces the two-argument overload; `RecentFood` is unchanged).

- [ ] **Step 1: Write the failing Web tests.** Add to `RepeatLoggingTests`:

```csharp
    [Fact]
    public async Task Recent_lists_what_this_account_eats_at_the_meal_first()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var banana = await SharedFoodAsync(s, "banana");
        var apple = (await s.Foods.SearchAsync("apple", 5, 0.3, default)).First(f => f.Id != banana).Id;
        for (var i = 0; i < 3; i++)
        {
            await s.FoodLog.AddAsync([Entry(apple, 150, Meal.Dinner, "Apple")], default);
        }

        await s.FoodLog.AddAsync([Entry(banana, 120, Meal.Breakfast)], default);

        (await s.FoodLog.RecentsAsync(Today, Meal.Breakfast, default)).Select(r => r.FoodItemId).Should().Equal(banana, apple);
        (await s.FoodLog.RecentsAsync(Today, Meal.Dinner, default)).Select(r => r.FoodItemId).Should().Equal(apple, banana);
        (await s.FoodLog.RecentsAsync(Today, Meal.Breakfast, default))[0].LastGrams.Should().Be(120);
    }

    [Fact]
    public async Task Recent_ignores_other_accounts()
    {
        await using var a = new UserScope(fixture, await fixture.CreateUserAsync());
        await using var b = new UserScope(fixture, await fixture.CreateUserAsync());
        await b.FoodLog.AddAsync([Entry(await SharedFoodAsync(b, "banana"), 120, Meal.Breakfast)], default);

        (await a.FoodLog.RecentsAsync(Today, Meal.Breakfast, default)).Should().BeEmpty();
    }
```

Change the three existing calls `RecentsAsync(Day, default)` in `NutritionTests.cs` (lines 182 and 202) and `QuickAddTests.cs` (line 22) to `RecentsAsync(Day, Meal.Lunch, default)`.

- [ ] **Step 2: Run them to see them fail.** `dotnet test tests/Plate.Web.Tests --filter "RepeatLoggingTests|NutritionTests|QuickAddTests"` → build error, no three-argument overload.

- [ ] **Step 3: Implement.** Replace `RecentsAsync` in `FoodLogService.cs` with:

```csharp
    /// <summary>
    /// Up to 20 foods from the last 30 days, what the account eats at <paramref name="meal"/> first (SPEC 5.10), each
    /// with the amount used last time.
    /// </summary>
    public async Task<List<RecentFood>> RecentsAsync(DateOnly today, Meal meal, CancellationToken ct)
    {
        var since = today.AddDays(-RecentOrder.Days);
        var rows = await db.FoodLog
            .Where(e => e.Date >= since && e.Grams > 0)
            .Select(e => new { e.Id, e.Meal, e.Date, e.CreatedAt, e.Description, e.FoodItemId, e.Grams, e.Kcal, e.ProteinG, e.CarbsG, e.FatG, e.FiberG, e.SaturatedFatG })
            .ToListAsync(ct);
        var byId = rows.ToDictionary(r => r.Id);

        return RecentOrder.For(rows.Select(r => new RecentUse(r.Id, r.FoodItemId, r.Description, r.Meal, r.Date, r.CreatedAt, r.Grams)), meal, today)
            .Select(g =>
            {
                var last = byId[g.LastEntryId];
                var f = 100m / last.Grams;
                return new RecentFood(last.Description, last.FoodItemId, last.Grams,
                    new Per100g(Math.Round(last.Kcal * f, 1), Math.Round(last.ProteinG * f, 2), Math.Round(last.CarbsG * f, 2), Math.Round(last.FatG * f, 2),
                        Round(last.FiberG * f, 2), Round(last.SaturatedFatG * f, 2)),
                    g.Times);
            })
            .ToList();
    }
```

In `FoodController.Add`, change `log.RecentsAsync(today, ct)` to `log.RecentsAsync(today, m, ct)`.

- [ ] **Step 4: Write and run an E2E check.** Add to `RepeatLoggingFlowTests`:

```csharp
    [Theory]
    [InlineData(E2EFixture.Chromium)]
    public async Task Recent_at_breakfast_lists_breakfast_foods_first(string browser)
    {
        var (_, porridge) = await ProductAsync();
        var (_, curry) = await ProductAsync();
        await using var phone = await app.SignedInPhoneAsync(browser);
        await using (var db = app.Db.CreateContext(null))
        {
            var ids = db.FoodItems.Where(f => f.Name == porridge || f.Name == curry).ToDictionary(f => f.Name, f => f.Id);
            for (var i = 0; i < 3; i++)
            {
                await phone.PostAsync("/food/log-item", new() { ["date"] = Today, ["meal"] = "Dinner", ["foodItemId"] = ids[curry].ToString(), ["grams"] = "300", ["source"] = "Search" });
            }

            await phone.PostAsync("/food/log-item", new() { ["date"] = Today, ["meal"] = "Breakfast", ["foodItemId"] = ids[porridge].ToString(), ["grams"] = "60", ["source"] = "Search" });
        }

        await phone.GoAsync($"/food/add?date={Today}&meal=Breakfast&tab=recent");
        (await phone.Page.Locator("form[action='/food/log-recent'] .row-title").First.TextContentAsync())!.Trim().Should().Be(porridge);
        phone.ShouldHaveNoErrors();
    }
```

Run `dotnet test tests/Plate.E2E.Tests --filter "RepeatLoggingFlowTests|ConfirmSheetTests|UntestedAreasTests"` → pass; Core and Web in full.

- [ ] **Step 5: Commit.** "Order Recent by what the account eats at the meal".

---

### Task 6: The parser says whether the amount was given

**Files:**
- Modify: `src/Plate.Core/Nutrition/FoodParsing.cs` (`ParsedFoodItem`)
- Modify: `src/Plate.Web/Integrations/OpenRouterFoodParser.cs` (prompt, schema, reading)
- Modify: `src/Plate.Web/Integrations/FakeFoodParser.cs`
- Test: `tests/Plate.Web.Tests/OpenRouterTests.cs`, `tests/Plate.Web.Tests/RepeatLoggingTests.cs`

**Interfaces:**
- Produces: `ParsedFoodItem.AmountGiven` (`bool`, last positional parameter, default `true`, so old stored `Parsed` JSON and existing callers mean "given" and nothing is ever replaced by accident).

- [ ] **Step 1: Write the failing tests.** Add to `OpenRouterTests` (the class holding `Sends_a_strict_schema_and_only_the_parameters_the_model_supports`):

```csharp
    [Fact]
    public async Task Reads_whether_the_amount_was_given()
    {
        var stub = new StubOpenRouter(completion: StubOpenRouter.Completion(
            """{"items":[{"nameEn":"cottage cheese","nameOriginal":"извара","grams":200,"amountGiven":false,"state":"na","preparation":null,"assumption":"приех 200 г","kcalPer100g":98,"proteinPer100g":11,"carbsPer100g":3.4,"fatPer100g":4.3,"fiberPer100g":null,"saturatedFatPer100g":null}]}"""));

        var result = await stub.Parser().ParseAsync("извара", "sk-or-v1-key", "google/gemini-3.5-flash-lite", default);

        result.Items.Single().AmountGiven.Should().BeFalse();
        var item = stub.SentBody!["response_format"]!["json_schema"]!["schema"]!["properties"]!["items"]!["items"]!;
        item["properties"]!["amountGiven"]!["type"]!.GetValue<string>().Should().Be("boolean");
        item["required"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain("amountGiven");

        var old = await new StubOpenRouter().Parser().ParseAsync("1 banana", "sk-or-v1-key", "google/gemini-3.5-flash-lite", default);
        old.Items.Single().AmountGiven.Should().BeTrue("an answer without the field never has its amount replaced");
    }
```

Add to `RepeatLoggingTests`:

```csharp
    [Theory]
    [InlineData("200г извара", true)]
    [InlineData("извара", false)]
    [InlineData("a banana", false)]
    [InlineData("една баница", false)]
    [InlineData("2 eggs", true)]
    [InlineData("две яйца", true)]
    public async Task The_fake_parser_marks_typed_amounts_and_counts(string text, bool given) =>
        (await new Plate.Web.Integrations.FakeFoodParser().ParseAsync(text, default)).Items.Single().AmountGiven.Should().Be(given);
```

- [ ] **Step 2: Run them to see them fail.** `dotnet test tests/Plate.Web.Tests --filter "Reads_whether_the_amount_was_given|The_fake_parser_marks"` → build error, no `AmountGiven`.

- [ ] **Step 3: The record.** In `FoodParsing.cs`, add a `<param>` line and the parameter:

```csharp
/// <param name="AmountGiven">
/// The text gives this food's amount as a weight, volume, count or fraction ("200 g", "2 eggs", "half a pack"); false when
/// it gives none ("извара", "a banana"). Only then may the account's last amount replace the grams (SPEC 6.1).
/// </param>
public record ParsedFoodItem(
    string NameEn,
    string NameOriginal,
    decimal Grams,
    FoodState State,
    string? Preparation,
    string? Assumption,
    Per100g Estimated,
    decimal? Units = null,
    bool AmountGiven = true);
```

- [ ] **Step 4: The OpenRouter parser.** In `Instructions`, after the `- grams:` line add:

```text
        - amountGiven: true when the text gives this food's amount as a weight, volume, count or fraction ("200 g", "2 eggs",
          "half a pack", "две филии"); false when it gives no amount ("извара", "some rice", "a banana": an article alone is not an amount).
```

In `Schema`, add `"amountGiven": { "type": "boolean" },` after `"grams"` and add `"amountGiven"` to `required` after `"grams"`. In the reading loop, construct the item with the named argument:

```csharp
                Estimate(node),
                AmountGiven: node["amountGiven"] is JsonValue given && given.TryGetValue<bool>(out var g) ? g : true);
```

- [ ] **Step 5: The fake parser.** In `FakeFoodParser.ParseAsync`, track whether an amount was read: set `var given = match.Success;` after the weight match, and inside the count branch set `given = !Article().IsMatch(count.Groups[1].Value);`. Pass `AmountGiven: given` when constructing the item. Add:

```csharp
    [GeneratedRegex(@"^(a|an|един|една|едно)$", RegexOptions.IgnoreCase)]
    private static partial Regex Article();
```

- [ ] **Step 6: Run the tests.** The two new tests pass; then Core and Web in full (`ParseRegressionTests` and `OpenRouterTests` must stay green).

- [ ] **Step 7: Commit.** "Have the parser say whether the amount was given".

---

### Task 7: Usual foods in matching, and the usual amount

**Files:**
- Modify: `src/Plate.Web/Features/Food/RepeatLoggingService.cs` (`UsualMatchesAsync`, `RememberAsync`)
- Modify: `src/Plate.Web/Features/Food/FoodMatcher.cs` (constructor, `ResolveAsync`, `ConfirmItem.Usual`)
- Modify: `src/Plate.Web/Features/Feedback/ParseRunService.cs` (constructor, `ResolvedRow.Usual`, `RememberAsync`)
- Modify: `src/Plate.Web/Controllers/FoodController.cs` (`Confirm`)
- Modify: `src/Plate.Web/Views/Food/Confirm.cshtml` (chip)
- Modify: `src/Plate.Web/Cli/FeedbackCommand.cs` (line 445)
- Modify: `src/Plate.Web/Resources/strings.bg.json`, `strings.keys.json`
- Modify: `tests/Plate.Web.Tests/UserScope.cs`
- Test: `tests/Plate.Web.Tests/RepeatLoggingTests.cs`, `tests/Plate.E2E.Tests/RepeatLoggingFlowTests.cs`

**Interfaces:**
- Consumes: `UsualFoods.Key` (Task 1), `AppDbContext.UsualFoods` (Task 2), `LastAmountsAsync` (Task 3), `ParsedFoodItem.AmountGiven` (Task 6).
- Produces: `Task<Dictionary<int, FoodMatch>> UsualMatchesAsync(IReadOnlyList<ParsedFoodItem> items, CancellationToken ct)` (item index → food); `Task RememberAsync(IReadOnlyList<(string Typed, int FoodItemId)> saved, CancellationToken ct)`; `FoodMatcher(FoodItemService foods, IFoodMatchDecider decider, RepeatLoggingService? repeat = null)`; `ConfirmItem.Usual` (`bool`, last parameter, default false); `ResolvedRow.Usual` (same); `ParseRunService(…, ILogger<ParseRunService> log, RepeatLoggingService? repeat = null)` with `Task RememberAsync(int? runId, IReadOnlyList<SavedItem> saved, CancellationToken ct)`.

- [ ] **Step 1: Wire the test scope.** In `UserScope.cs`, move the `Repeat = …` line above `Matcher = …`, construct the matcher as `new Plate.Web.Features.Food.FoodMatcher(Foods, MatchDecider, Repeat)`, and add `, Repeat` as the last argument of the `ParseRunService` constructor call.

- [ ] **Step 2: Write the failing Web tests.** Add to `RepeatLoggingTests`:

```csharp
    /// <summary>Parses, saves the first row as <paramref name="foodItemId"/> and remembers it, as the Confirm action does.</summary>
    private static async Task ConfirmAsync(UserScope s, string text, int foodItemId, bool include = true)
    {
        var outcome = await s.ParseRuns.ParseAsync(text, default);
        var row = outcome.Items[0];
        var saved = outcome.Items.Select((r, n) => new SavedItem(n == 0 ? include : false, r.Description, r.Grams, r.Per100, n == 0 ? foodItemId : r.FoodItemId)).ToList();
        var runId = await s.ParseRuns.ConfirmAsync(outcome.RunId, saved, default);
        if (include)
        {
            await s.FoodLog.AddAsync([new NewEntry(Today, Meal.Lunch, row.Description, row.Grams, row.Per100, foodItemId, EntrySource.Ai, ParseRunId: runId)], default);
        }

        await s.ParseRuns.RememberAsync(runId, saved, default);
    }

    /// <summary>A shared "извара" food that the ranking does not pick first, so only a remembered choice can explain it.</summary>
    private static async Task<int> HandPickedAsync(UserScope s)
    {
        var ranked = (await s.ParseRuns.ParseAsync("200г извара", default)).Items[0].FoodItemId;
        return (await s.Foods.SearchAsync("извара", 20, 0.3, default)).First(f => f.Id != ranked && !f.Own).Id;
    }

    [Fact]
    public async Task A_confirmed_food_is_proposed_again_for_the_same_words_with_its_amount()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var picked = await HandPickedAsync(s);
        await ConfirmAsync(s, "200г извара", picked);
        s.MatchDecider.Calls.Clear();

        var outcome = await s.ParseRuns.ParseAsync("Извара and banana", default);
        var item = outcome.Items[0];

        item.FoodItemId.Should().Be(picked);
        item.Usual.Should().BeTrue();
        item.Confidence.Should().Be(Confidence.High);
        item.Grams.Should().Be(200);
        item.Notes.Should().ContainSingle(n => n.Format == "Your usual amount, {0} g");
        s.MatchDecider.Calls.Single()[0].Candidates.Should().BeEmpty("a usual food is context for the decider, not a question");
        var run = await s.Db.ParseRuns.SingleAsync(r => r.Id == outcome.RunId);
        Plate.Web.Features.Feedback.FeedbackJson.Read<List<Plate.Web.Features.Feedback.ResolvedRow>>(run.Resolved)![0].Usual.Should().BeTrue();
    }

    [Fact]
    public async Task A_typed_amount_is_kept()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var picked = await HandPickedAsync(s);
        await ConfirmAsync(s, "200г извара", picked);

        var item = (await s.ParseRuns.ParseAsync("300г извара", default)).Items.Single();

        (item.FoodItemId, item.Grams, item.Usual).Should().Be(((int?)picked, 300m, true));
        item.Notes.Should().NotContain(n => n.Format == "Your usual amount, {0} g");
    }

    [Fact]
    public async Task Picking_another_food_once_moves_the_words_to_it()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var first = await HandPickedAsync(s);
        var second = (await s.Foods.SearchAsync("извара", 20, 0.3, default)).First(f => f.Id != first && !f.Own).Id;
        await ConfirmAsync(s, "200г извара", first);
        await ConfirmAsync(s, "извара", second);

        (await s.ParseRuns.ParseAsync("извара", default)).Items.Single().FoodItemId.Should().Be(second);
        (await s.Db.UsualFoods.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task An_unticked_row_writes_nothing()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await ConfirmAsync(s, "200г извара", await HandPickedAsync(s), include: false);

        (await s.Db.UsualFoods.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_same_words_twice_in_one_parse_write_one_row()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var a = await SharedFoodAsync(s, "banana");
        var b = (await s.Foods.SearchAsync("apple", 5, 0.3, default)).First(f => f.Id != a).Id;

        await s.Repeat.RememberAsync([("200г банан", a), ("банан", b)], default);

        (await s.Db.UsualFoods.SingleAsync()).FoodItemId.Should().Be(b, "the later row wins");
    }

    [Fact]
    public async Task Remembering_twice_keeps_one_row()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var food = await SharedFoodAsync(s, "banana");
        await s.Repeat.RememberAsync([("банан", food)], default);
        await using var again = new UserScope(fixture, s.UserId);

        await again.Repeat.RememberAsync([("банан", food)], default);

        (await s.Db.UsualFoods.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_usual_food_pointing_at_a_food_this_account_cannot_see_is_ignored()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await using var other = new UserScope(fixture, await fixture.CreateUserAsync());
        var foreign = await other.Foods.CreateManualAsync("Извара от село", null, new Per100g(120, 14, 3, 6), null, share: false, default);
        s.Db.UsualFoods.Add(new UsualFood { UserId = s.UserId, Phrase = "извара", FoodItemId = foreign.Id, LastUsedAt = DateTimeOffset.UtcNow });
        await s.Db.SaveChangesAsync();

        var item = (await s.ParseRuns.ParseAsync("извара", default)).Items.Single();

        item.FoodItemId.Should().NotBe(foreign.Id);
        item.Usual.Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_the_food_removes_its_usual_food()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var mine = await s.Foods.CreateManualAsync("Извара Верея", null, new Per100g(98, 11, 3.4m, 4.3m), null, share: false, default);
        await s.Repeat.RememberAsync([("извара", mine.Id)], default);

        s.Db.FoodItems.Remove(mine);
        await s.Db.SaveChangesAsync();

        (await s.Db.UsualFoods.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Without_a_run_nothing_is_remembered()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await s.ParseRuns.RememberAsync(null, [new SavedItem(true, "Banana", 120, new Per100g(89, 1, 23, 0), await SharedFoodAsync(s, "banana"))], default);

        (await s.Db.UsualFoods.CountAsync()).Should().Be(0);
    }
```

- [ ] **Step 3: Run them to see them fail.** `dotnet test tests/Plate.Web.Tests --filter RepeatLoggingTests` → build errors (`Usual`, `RememberAsync`).

- [ ] **Step 4: The service.** Add to `RepeatLoggingService` (add `using Plate.Core.Domain;`):

```csharp
    /// <summary>
    /// The food this account means by each item's words (SPEC 6.1 step 1), by item index: its <c>NameOriginal</c> key, else
    /// its <c>NameEn</c> key. A row whose food the account can no longer see is skipped.
    /// </summary>
    public async Task<Dictionary<int, FoodMatch>> UsualMatchesAsync(IReadOnlyList<ParsedFoodItem> items, CancellationToken ct)
    {
        var keys = items.Select(i => new[] { UsualFoods.Key(i.NameOriginal), UsualFoods.Key(i.NameEn) }).ToList();
        var all = keys.SelectMany(k => k).OfType<string>().Distinct().ToList();
        if (all.Count == 0)
        {
            return [];
        }

        var byPhrase = await db.UsualFoods.Where(u => all.Contains(u.Phrase)).ToDictionaryAsync(u => u.Phrase, u => u.FoodItemId, ct);
        var ids = byPhrase.Values.Distinct().ToList();
        var foods = await db.FoodItems.Where(f => ids.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct); // own or shared only

        var result = new Dictionary<int, FoodMatch>();
        for (var n = 0; n < items.Count; n++)
        {
            foreach (var key in keys[n])
            {
                if (key is not null && byPhrase.TryGetValue(key, out var id) && foods.TryGetValue(id, out var food))
                {
                    result[n] = Match(food);
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Remembers which food the account saved for its words (SPEC 6.1, usual foods): the latest save wins. Recording
    /// never gets in the way of logging food, so a failure is logged and dropped.
    /// </summary>
    public async Task RememberAsync(IReadOnlyList<(string Typed, int FoodItemId)> saved, CancellationToken ct)
    {
        var keyed = saved
            .Select(s => (Key: UsualFoods.Key(s.Typed), s.FoodItemId))
            .Where(s => s.Key is not null)
            .GroupBy(s => s.Key!)
            .Select(g => (Key: g.Key, g.Last().FoodItemId))
            .ToList();
        if (keyed.Count == 0)
        {
            return;
        }

        try
        {
            var ids = keyed.Select(k => k.FoodItemId).Distinct().ToList();
            var visible = (await db.FoodItems.Where(f => ids.Contains(f.Id)).Select(f => f.Id).ToListAsync(ct)).ToHashSet();
            var phrases = keyed.Select(k => k.Key).ToList();
            var existing = await db.UsualFoods.Where(u => phrases.Contains(u.Phrase)).ToDictionaryAsync(u => u.Phrase, ct);
            var now = time.GetUtcNow();
            foreach (var (key, foodItemId) in keyed.Where(k => visible.Contains(k.FoodItemId)))
            {
                if (existing.TryGetValue(key, out var row))
                {
                    row.FoodItemId = foodItemId;
                    row.LastUsedAt = now;
                }
                else
                {
                    db.UsualFoods.Add(new UsualFood { UserId = currentUser.UserId!, Phrase = key, FoodItemId = foodItemId, LastUsedAt = now });
                }
            }

            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Two confirms at once can both insert the same words; the food is already logged, so this only loses the memory.
            log.LogWarning(ex, "Could not remember usual foods.");
            foreach (var entry in db.ChangeTracker.Entries<UsualFood>().ToList())
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    private FoodMatch Match(FoodItem f) =>
        new(f.Id, f.Name, f.NameBg, f.Brand, f.KcalPer100g, f.ProteinPer100g, f.CarbsPer100g, f.FatPer100g, f.FiberPer100g, f.SaturatedFatPer100g,
            f.DefaultServingGrams, Own: f.OwnerUserId is not null, ByOther: FoodItemService.AddedByAnother(f, currentUser.UserId), Score: 1);
```

- [ ] **Step 5: The matcher.** In `FoodMatcher.cs`:
  - Add `bool Usual = false` as the last parameter of `ConfirmItem`, with `/// <param name="Usual">Matched through the account's usual food for these words (SPEC 6.1 step 1).</param>`.
  - Change the class declaration to `public class FoodMatcher(FoodItemService foods, IFoodMatchDecider decider, RepeatLoggingService? repeat = null)` and add to the summary: "Without <paramref name=\"repeat\"/> (the evaluation), no usual foods or last amounts are used, so the parser and matcher are measured alone."
  - At the start of `ResolveAsync`, after `var items = parsed.ToList();`:

```csharp
        var usual = repeat is null ? [] : await repeat.UsualMatchesAsync(items, ct);
```

  - In the loop that empties shortlists for own foods, change the condition to `if (usual.ContainsKey(n) || ranked[n] is { Food.Own: true })` and the comment's first line to "A usual food, or a food the user saved at the top of the ranking, stands: …".
  - In the result loop, right after the `trace?.Add(...)` call:

```csharp
            if (usual.TryGetValue(n, out var mine))
            {
                result.Add(Matched(item, mine, Portion(item, mine, notes), Confidence.High, notes, null) with { Usual = true });
                continue;
            }
```

  - Before `return result;` at the end of `ResolveAsync`:

```csharp
        if (repeat is not null)
        {
            await UseLastAmountsAsync(items, result, ct);
        }
```

  - Add the methods:

```csharp
    private static readonly string[] AmountNotes = ["{0} × a serving of {1} g", "assumed 100 g each"];

    /// <summary>
    /// SPEC 6.1 step 4: when the text gave no amount, a matched food takes the account's last amount, and the note
    /// about the guessed amount gives way to "Your usual amount". An amount the user gave is never replaced.
    /// </summary>
    private async Task UseLastAmountsAsync(List<ParsedFoodItem> items, List<ConfirmItem> result, CancellationToken ct)
    {
        var open = Enumerable.Range(0, result.Count).Where(n => !items[n].AmountGiven && result[n].FoodItemId is not null).ToList();
        if (open.Count == 0)
        {
            return;
        }

        var last = await repeat!.LastAmountsAsync(open.Select(n => result[n].FoodItemId!.Value).ToList(), ct);
        foreach (var n in open)
        {
            if (last.TryGetValue(result[n].FoodItemId!.Value, out var grams))
            {
                var notes = result[n].Notes
                    .Where(note => note.Format != items[n].Assumption && !AmountNotes.Contains(note.Format))
                    .Append(new LocalText("Your usual amount, {0} g", grams.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)))
                    .ToList();
                result[n] = result[n] with { Grams = grams, Notes = notes };
            }
        }
    }
```

  `ParseEvaluation` keeps `new FoodMatcher(foods, decider)`: no change there.

- [ ] **Step 6: The run and the confirm.** In `ParseRunService.cs`:
  - Add `bool Usual = false` as the last parameter of `ResolvedRow` and pass `item.Usual` in the `Resolved = …` projection (after `item.Status`).
  - Add `RepeatLoggingService? repeat = null` as the last constructor parameter.
  - Add:

```csharp
    /// <summary>
    /// Remembers the food saved for each typed name (SPEC 6.1, usual foods), from the run's parsed names. Without a run
    /// (missing or someone else's) there are no typed names, so nothing is remembered.
    /// </summary>
    public async Task RememberAsync(int? runId, IReadOnlyList<SavedItem> saved, CancellationToken ct)
    {
        if (repeat is null || runId is not { } id || await db.ParseRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct) is not { } run)
        {
            return;
        }

        var parsed = FeedbackJson.Read<List<ParsedFoodItem>>(run.Parsed) ?? [];
        var pairs = saved.Take(parsed.Count)
            .Select((s, n) => (Saved: s, Typed: parsed[n].NameOriginal))
            .Where(x => x.Saved.Included && x.Saved.FoodItemId is not null)
            .Select(x => (x.Typed, x.Saved.FoodItemId!.Value))
            .ToList();
        await repeat.RememberAsync(pairs, ct);
    }
```

  In `FoodController.Confirm`, keep the `SavedItem` list in a variable (`var saved = items.Select(...).ToList();`, passed to `ConfirmAsync`), and after `await log.AddAsync(entries, ct);` add `await parseRuns.RememberAsync(runId, saved, ct);`.

- [ ] **Step 7: The chip and the CLI.** In `Confirm.cshtml`, replace the `var (chip, label) = item.Confidence switch { … };` statement with:

```csharp
            var (chip, label) = item.Usual ? ("good", L["Your usual"]) : item.Confidence switch
            {
                Confidence.High => ("good", L["Database"]),
                Confidence.Medium => ("check", L["Check match"]),
                _ => ("ai", L["AI estimate"]),
            };
```

In `FeedbackCommand.cs` line 445, append the mark after `{row["confidence"]}`:

```csharp
            o.WriteLine($" {mark}[{n}] {row["description"]} {row["grams"]} g → {row["matchedName"] ?? "(AI estimate)"} #{row["foodItemId"]} {row["confidence"]}{(row["usual"]?.GetValue<bool>() == true ? " usual" : "")}");
```

- [ ] **Step 8: The strings.** Add `"Your usual": "Обичайно"` and `"Your usual amount, {0} g": "Обичайното ти количество, {0} г"`; regenerate keys.

- [ ] **Step 9: Run the Web tests.** `dotnet test tests/Plate.Web.Tests --filter "RepeatLoggingTests|FoodMatchingTests|ParseRunTests|ParseRegressionTests|FallbackMatchTests|JevTests|CatalogueTests"` → pass.

- [ ] **Step 10: Write and run the E2E test.** Add to `RepeatLoggingFlowTests`:

```csharp
    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Typed_words_propose_the_food_confirmed_for_them_last_time(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=text");
        await phone.Page.Locator("textarea[name=text], input[name=text]").First.FillAsync("200 g cottage cheese");
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/parse'] button[type=submit]").First.TapAsync());
        var matched = await phone.Page.Locator(".result").First.Locator("textarea[name$='Description']").InputValueAsync();
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/confirm'] button[type=submit]").Last.TapAsync());

        await phone.GoAsync($"/food/add?date={Today}&meal=Dinner&tab=text");
        await phone.Page.Locator("textarea[name=text], input[name=text]").First.FillAsync("cottage cheese");
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/parse'] button[type=submit]").First.TapAsync());

        var row = phone.Page.Locator(".result").First;
        (await row.Locator("[data-chip]").TextContentAsync())!.Trim().Should().Be("Your usual");
        (await row.Locator("input[name$='Grams']").InputValueAsync()).Should().Be("200");
        (await row.Locator("[data-note]").AllTextContentsAsync()).Should().Contain("Your usual amount, 200 g");
        (await row.Locator("textarea[name$='Description']").InputValueAsync()).Should().Be(matched);
        phone.ShouldHaveNoErrors();
    }
```

Run `dotnet test tests/Plate.E2E.Tests --filter "RepeatLoggingFlowTests|ConfirmFoodsTests|AiPathTests"` → pass; Core and Web in full.

- [ ] **Step 11: Commit.** "Propose the food last confirmed for typed words, with the usual amount".

---

### Task 8: Full verification

**Files:** none changed unless a check fails.

- [ ] **Step 1: Full suites.** `dotnet build` (no warnings added), `dotnet test tests/Plate.Core.Tests`, `dotnet test tests/Plate.Web.Tests`, `dotnet test tests/Plate.E2E.Tests`. Report the counts.

- [ ] **Step 2: Bulgarian at 320 px.** With the dev stack running (`docker compose -f docker/docker-compose.dev.yml up -d db`, `dotnet run --project src/Plate.Web`), open a barcode result for a product logged before, as a Bulgarian account, at 320 and 390 px in Chromium and WebKit (an E2E-style script or the browser tools): "Последно" under the field, "Добави и сканирай още" fits on one line or wraps inside the button without sideways scroll, and the confirmation chip "Обичайно" fits beside the status chip. Fix and commit any overflow as its own commit.

- [ ] **Step 3: Leave for Antoan.** The iPhone checks from the Phase 16 "Done when" (390 px, English and Bulgarian, the real camera through "Add and scan another"), the merge, the deploy with a pre-deploy dump, and the What's new entries are his to approve; list them in the final report with the test counts.
