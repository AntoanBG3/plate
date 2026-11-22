using Plate.Core.Text;
using Plate.Core.Domain;
using Plate.Core.Nutrition;
using Plate.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Features.Food;

public record NewEntry(DateOnly Date, Meal Meal, string Description, decimal Grams, Per100g Per100, int? FoodItemId, EntrySource Source, string? AiModel = null, string? MatchModel = null, int? ParseRunId = null, string? Status = null);

/// <summary>A frequently logged food for one-tap re-logging.</summary>
public record RecentFood(string Description, int? FoodItemId, decimal LastGrams, Per100g Per100, int Times, FoodUnit Unit = FoodUnit.Grams);

/// <summary>The This week card: averages and the targets they are judged against.</summary>
public record WeekCard(WeekSummary Week, MacroTarget Targets, int? TargetKcal);

public record DayLog(DateOnly Date, IReadOnlyList<FoodLogEntry> Entries, Totals Totals)
{
    public IEnumerable<FoodLogEntry> For(Meal meal) => Entries.Where(e => e.Meal == meal);

    /// <summary>Fiber and saturated fat with how much of each is known (SPEC 5.8).</summary>
    public DayQuality Quality { get; } = DayQuality.Of(Entries);

    public Totals TotalsFor(Meal meal) =>
        For(meal).Aggregate(Totals.Zero, (t, e) => t.Add(FoodMath.TotalsOf(e)));
}

public class FoodLogService(AppDbContext db, ICurrentUser currentUser, TimeProvider time)
{
    public async Task<DayLog> DayAsync(DateOnly date, CancellationToken ct)
    {
        var entries = await db.FoodLog
            .AsNoTracking()
            .Include(e => e.FoodItem) // its name says raw or cooked for entries without a saved status
            .Where(e => e.Date == date)
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
            .ToListAsync(ct);
        var totals = entries.Aggregate(Totals.Zero, (t, e) => t.Add(FoodMath.TotalsOf(e)));
        return new DayLog(date, entries, totals);
    }

    /// <summary>Each logged day of the week before <paramref name="today"/>, for the weekly averages (SPEC 5.8).</summary>
    public Task<List<DaySummary>> WeekBeforeAsync(DateOnly today, CancellationToken ct) =>
        DaysBetweenAsync(today.AddDays(-WeeklyNutrition.Days), today, ct);

    /// <summary>Each logged day from <paramref name="from"/> up to, not including, <paramref name="until"/>.</summary>
    public async Task<List<DaySummary>> DaysBetweenAsync(DateOnly from, DateOnly until, CancellationToken ct)
    {
        var entries = await db.FoodLog.AsNoTracking().Where(e => e.Date >= from && e.Date < until).ToListAsync(ct);
        return entries
            .GroupBy(e => e.Date)
            .Select(g => new DaySummary(g.Key, g.Aggregate(Totals.Zero, (t, e) => t.Add(FoodMath.TotalsOf(e))), DayQuality.Of(g.ToList())))
            .ToList();
    }

    /// <summary>The weekly averages against today's targets; null without targets or with too few logged days.</summary>
    public async Task<WeekCard?> WeekCardAsync(DateOnly today, DailyTargets targets, CancellationToken ct) =>
        targets.Macros is { } macros && WeeklyNutrition.Calculate(await WeekBeforeAsync(today, ct), macros, today) is { } week
            ? new WeekCard(week, macros, targets.Kcal)
            : null;

    public async Task<FoodLogEntry> GetAsync(int id, CancellationToken ct) =>
        await db.FoodLog.SingleOrDefaultAsync(e => e.Id == id, ct) ?? throw new NotFoundException();

    /// <summary>Saves several entries at once, or none if any is invalid.</summary>
    public async Task AddAsync(IReadOnlyList<NewEntry> entries, CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new InvalidOperationException("No signed-in user.");
        foreach (var date in entries.Select(e => e.Date).Distinct())
        {
            await FoodDays.EnsureNotFutureAsync(db, time, date, ct);
        }

        foreach (var e in entries)
        {
            if (FoodMath.Validate(e.Grams, e.Per100) is { } error)
            {
                throw new ValidationException(new LocalText("{0}: {1}", e.Description, error));
            }

            // An entry is counted in its food's unit (SPEC 4): ml for a food from a per-100 ml label.
            var unit = FoodUnit.Grams;
            if (e.FoodItemId is { } id)
            {
                unit = await db.FoodItems.Where(f => f.Id == id).Select(f => (FoodUnit?)f.Unit).SingleOrDefaultAsync(ct)
                    ?? throw new NotFoundException("Food not found.");
            }

            var totals = FoodMath.For(e.Per100, e.Grams);
            db.FoodLog.Add(new FoodLogEntry
            {
                UserId = userId,
                Date = e.Date,
                Meal = e.Meal,
                Description = CleanDescription(e.Description),
                Grams = Math.Round(e.Grams, 1),
                Unit = unit,
                Kcal = totals.Kcal,
                ProteinG = totals.ProteinG,
                CarbsG = totals.CarbsG,
                FatG = totals.FatG,
                FiberG = totals.FiberG,
                SaturatedFatG = totals.SaturatedFatG,
                FoodItemId = e.FoodItemId,
                Source = e.Source,
                AiModel = e.AiModel,
                MatchModel = e.MatchModel is { Length: > 100 } m ? m[..100] : e.MatchModel,
                ParseRunId = e.ParseRunId,
                Status = CleanStatus(e.Status),
                CreatedAt = time.GetUtcNow(),
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Logs calories and macros typed directly, with no food item and no grams.</summary>
    public async Task AddQuickAsync(DateOnly date, Meal meal, string description, decimal kcal, decimal protein, decimal carbs, decimal fat, decimal? fiber, decimal? saturatedFat, CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new InvalidOperationException("No signed-in user.");
        await FoodDays.EnsureNotFutureAsync(db, time, date, ct);
        if (FoodMath.ValidateQuick(kcal, protein, carbs, fat, fiber, saturatedFat) is { } error)
        {
            throw new ValidationException(error);
        }

        db.FoodLog.Add(new FoodLogEntry
        {
            UserId = userId,
            Date = date,
            Meal = meal,
            Description = CleanQuickName(description),
            Grams = 0,
            Kcal = Math.Round(kcal, 1),
            ProteinG = Math.Round(protein, 1),
            CarbsG = Math.Round(carbs, 1),
            FatG = Math.Round(fat, 1),
            FiberG = Round(fiber),
            SaturatedFatG = Round(saturatedFat),
            Source = EntrySource.Quick,
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Changes a quick entry's name, meal and numbers. Quick entries have no amount to scale.</summary>
    public async Task UpdateQuickAsync(int id, string description, Meal meal, decimal kcal, decimal protein, decimal carbs, decimal fat, decimal? fiber, decimal? saturatedFat, CancellationToken ct)
    {
        var entry = await GetAsync(id, ct);
        if (entry.Source != EntrySource.Quick)
        {
            throw new ValidationException("Change this entry's amount instead.");
        }

        if (FoodMath.ValidateQuick(kcal, protein, carbs, fat, fiber, saturatedFat) is { } error)
        {
            throw new ValidationException(error);
        }

        entry.Description = CleanQuickName(description);
        entry.Meal = meal;
        (entry.Kcal, entry.ProteinG, entry.CarbsG, entry.FatG) = (Math.Round(kcal, 1), Math.Round(protein, 1), Math.Round(carbs, 1), Math.Round(fat, 1));
        (entry.FiberG, entry.SaturatedFatG) = (Round(fiber), Round(saturatedFat));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Changes an entry's name, meal or amount; nutrition scales with the new amount.</summary>
    public async Task UpdateAsync(int id, string description, decimal grams, Meal meal, CancellationToken ct)
    {
        var entry = await GetAsync(id, ct);
        if (entry.Source == EntrySource.Quick)
        {
            throw new ValidationException("Change a quick entry's calories instead of its amount.");
        }

        var per100 = PerHundred(entry);
        if (FoodMath.Validate(grams, per100) is { } error)
        {
            throw new ValidationException(error);
        }

        var totals = FoodMath.For(per100, grams);
        entry.Description = CleanDescription(description);
        entry.Meal = meal;
        entry.Grams = Math.Round(grams, 1);
        (entry.Kcal, entry.ProteinG, entry.CarbsG, entry.FatG) = (totals.Kcal, totals.ProteinG, totals.CarbsG, totals.FatG);
        (entry.FiberG, entry.SaturatedFatG) = (totals.FiberG, totals.SaturatedFatG);
        await db.SaveChangesAsync(ct);
    }

    public async Task<DateOnly> DeleteAsync(int id, CancellationToken ct)
    {
        var entry = await GetAsync(id, ct);
        entry.DeletedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return entry.Date;
    }

    /// <summary>
    /// Up to 20 foods from the last 30 days, what the account eats at <paramref name="meal"/> first (SPEC 5.10), each
    /// with the amount used last time.
    /// </summary>
    public async Task<List<RecentFood>> RecentsAsync(DateOnly today, Meal meal, CancellationToken ct)
    {
        var since = today.AddDays(-RecentOrder.Days);
        var rows = await db.FoodLog
            .Where(e => e.Date >= since && e.Grams > 0)
            .Select(e => new { e.Id, e.Meal, e.Date, e.CreatedAt, e.Description, e.FoodItemId, e.Grams, e.Unit, e.Kcal, e.ProteinG, e.CarbsG, e.FatG, e.FiberG, e.SaturatedFatG })
            .ToListAsync(ct);
        var byId = rows.ToDictionary(r => r.Id);
        // A recipe can be edited, so Recent offers its values now, not the ones its last entry was logged with.
        var foodIds = rows.Select(r => r.FoodItemId).OfType<int>().Distinct().ToList();
        var recipes = await db.FoodItems.Where(f => foodIds.Contains(f.Id) && f.Source == FoodSource.Recipe).ToDictionaryAsync(f => f.Id, ct);

        return RecentOrder.For(rows.Select(r => new RecentUse(r.Id, r.FoodItemId, r.Description, r.Meal, r.Date, r.CreatedAt, r.Grams)), meal, today)
            .Select(g =>
            {
                var last = byId[g.LastEntryId];
                if (last.FoodItemId is { } recipeId && recipes.TryGetValue(recipeId, out var recipe))
                {
                    return new RecentFood(last.Description, last.FoodItemId, last.Grams, recipe.Per100(), g.Times);
                }

                var f = 100m / last.Grams;
                return new RecentFood(last.Description, last.FoodItemId, last.Grams,
                    new Per100g(Math.Round(last.Kcal * f, 1), Math.Round(last.ProteinG * f, 2), Math.Round(last.CarbsG * f, 2), Math.Round(last.FatG * f, 2),
                        Round(last.FiberG * f, 2), Round(last.SaturatedFatG * f, 2)),
                    g.Times, last.Unit);
            })
            .ToList();
    }

    /// <summary>Copies one meal's entries from one day to another. Returns how many were copied.</summary>
    public async Task<int> CopyMealAsync(DateOnly from, DateOnly to, Meal meal, CancellationToken ct)
    {
        await FoodDays.EnsureNotFutureAsync(db, time, to, ct);
        var source = await db.FoodLog.Where(e => e.Date == from && e.Meal == meal).ToListAsync(ct);
        var now = time.GetUtcNow();
        foreach (var e in source)
        {
            db.FoodLog.Add(new FoodLogEntry
            {
                UserId = e.UserId,
                Date = to,
                Meal = meal,
                Description = e.Description,
                Grams = e.Grams,
                Unit = e.Unit,
                Kcal = e.Kcal,
                ProteinG = e.ProteinG,
                CarbsG = e.CarbsG,
                FatG = e.FatG,
                FiberG = e.FiberG,
                SaturatedFatG = e.SaturatedFatG,
                FoodItemId = e.FoodItemId,
                Status = e.Status,
                Source = e.Source == EntrySource.Quick ? EntrySource.Quick : EntrySource.Recent,
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
        return source.Count;
    }

    private static Per100g PerHundred(FoodLogEntry e)
    {
        if (e.Grams <= 0)
        {
            return new Per100g(0, 0, 0, 0);
        }

        var f = 100m / e.Grams;
        return new Per100g(e.Kcal * f, e.ProteinG * f, e.CarbsG * f, e.FatG * f, e.FiberG * f, e.SaturatedFatG * f);
    }

    private static decimal? Round(decimal? value, int decimals = 1) => value is { } v ? Math.Round(v, decimals) : null;

    /// <summary>A short status as the form sent it, or null; anything too long is dropped rather than refused.</summary>
    private static string? CleanStatus(string? status)
    {
        var s = string.Join(' ', (status ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length is 0 or > FoodNaming.MaxStatusLength ? null : s;
    }

    /// <summary>A trimmed name without a leading amount ("150 g rice" is "rice"; the grams are stored apart).</summary>
    private static string CleanDescription(string? description)
    {
        // The Check foods name field is a growing text area; a line break typed into it is a space.
        var d = FoodText.StripAmount(string.Join(' ', (description ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        if (d.Length is 0 or > 200)
        {
            throw new ValidationException("Description must be 1 to 200 characters.");
        }

        return d;
    }

    /// <summary>A quick entry's name may be left empty; it then reads "Quick add" in the reader's language.</summary>
    private static string CleanQuickName(string? description)
    {
        var d = description?.Trim() ?? "";
        if (d.Length > 200)
        {
            throw new ValidationException("Description must be 1 to 200 characters.");
        }

        return d;
    }
}
