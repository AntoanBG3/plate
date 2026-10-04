using GymTracker.Core.Text;
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Food;

public record NewEntry(DateOnly Date, Meal Meal, string Description, decimal Grams, Per100g Per100, int? FoodItemId, EntrySource Source, string? AiModel = null, string? MatchModel = null, int? ParseRunId = null);

/// <summary>A frequently logged food for one-tap re-logging.</summary>
public record RecentFood(string Description, int? FoodItemId, decimal LastGrams, Per100g Per100, int Times);

public record DayLog(DateOnly Date, IReadOnlyList<FoodLogEntry> Entries, Totals Totals)
{
    public IEnumerable<FoodLogEntry> For(Meal meal) => Entries.Where(e => e.Meal == meal);

    public Totals TotalsFor(Meal meal) =>
        For(meal).Aggregate(Totals.Zero, (t, e) => t.Add(new Totals(e.Kcal, e.ProteinG, e.CarbsG, e.FatG)));
}

public class FoodLogService(AppDbContext db, ICurrentUser currentUser, TimeProvider time)
{
    public async Task<DayLog> DayAsync(DateOnly date, CancellationToken ct)
    {
        var entries = await db.FoodLog
            .AsNoTracking()
            .Where(e => e.Date == date)
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
            .ToListAsync(ct);
        var totals = entries.Aggregate(Totals.Zero, (t, e) => t.Add(new Totals(e.Kcal, e.ProteinG, e.CarbsG, e.FatG)));
        return new DayLog(date, entries, totals);
    }

    public async Task<FoodLogEntry> GetAsync(int id, CancellationToken ct) =>
        await db.FoodLog.SingleOrDefaultAsync(e => e.Id == id, ct) ?? throw new NotFoundException();

    /// <summary>Saves several entries at once, or none if any is invalid.</summary>
    public async Task AddAsync(IReadOnlyList<NewEntry> entries, CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new InvalidOperationException("No signed-in user.");
        foreach (var e in entries)
        {
            if (FoodMath.Validate(e.Grams, e.Per100) is { } error)
            {
                throw new ValidationException(new LocalText("{0}: {1}", e.Description, error));
            }

            if (e.FoodItemId is { } id && !await db.FoodItems.AnyAsync(f => f.Id == id, ct))
            {
                throw new NotFoundException("Food not found.");
            }

            var totals = FoodMath.For(e.Per100, e.Grams);
            db.FoodLog.Add(new FoodLogEntry
            {
                UserId = userId,
                Date = e.Date,
                Meal = e.Meal,
                Description = CleanDescription(e.Description),
                Grams = Math.Round(e.Grams, 1),
                Kcal = totals.Kcal,
                ProteinG = totals.ProteinG,
                CarbsG = totals.CarbsG,
                FatG = totals.FatG,
                FoodItemId = e.FoodItemId,
                Source = e.Source,
                AiModel = e.AiModel,
                MatchModel = e.MatchModel is { Length: > 100 } m ? m[..100] : e.MatchModel,
                ParseRunId = e.ParseRunId,
                CreatedAt = time.GetUtcNow(),
            });
        }

        await db.SaveChangesAsync(ct);
    }

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

    /// <summary>Changes a quick entry's name, meal and numbers. Quick entries have no amount to scale.</summary>
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
        await db.SaveChangesAsync(ct);
    }

    public async Task<DateOnly> DeleteAsync(int id, CancellationToken ct)
    {
        var entry = await GetAsync(id, ct);
        entry.DeletedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return entry.Date;
    }

    /// <summary>The 20 foods logged most often in the last 30 days, with the amount used last time.</summary>
    public async Task<List<RecentFood>> RecentsAsync(DateOnly today, CancellationToken ct)
    {
        var since = today.AddDays(-30);
        var rows = await db.FoodLog
            .Where(e => e.Date >= since && e.Grams > 0)
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.CreatedAt)
            .Select(e => new { e.Description, e.FoodItemId, e.Grams, e.Kcal, e.ProteinG, e.CarbsG, e.FatG })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => (r.FoodItemId, Key: r.Description.ToLowerInvariant()))
            .Select(g =>
            {
                var last = g.First();
                var f = 100m / last.Grams;
                return new RecentFood(last.Description, last.FoodItemId, last.Grams,
                    new Per100g(Math.Round(last.Kcal * f, 1), Math.Round(last.ProteinG * f, 2), Math.Round(last.CarbsG * f, 2), Math.Round(last.FatG * f, 2)),
                    g.Count());
            })
            .OrderByDescending(r => r.Times).ThenBy(r => r.Description)
            .Take(20)
            .ToList();
    }

    /// <summary>Copies one meal's entries from one day to another. Returns how many were copied.</summary>
    public async Task<int> CopyMealAsync(DateOnly from, DateOnly to, Meal meal, CancellationToken ct)
    {
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
                Kcal = e.Kcal,
                ProteinG = e.ProteinG,
                CarbsG = e.CarbsG,
                FatG = e.FatG,
                FoodItemId = e.FoodItemId,
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
        return new Per100g(e.Kcal * f, e.ProteinG * f, e.CarbsG * f, e.FatG * f);
    }

    private static string CleanDescription(string? description)
    {
        var d = description?.Trim() ?? "";
        if (d.Length is 0 or > 200)
        {
            throw new ValidationException("Description must be 1 to 200 characters.");
        }

        return d;
    }
}
