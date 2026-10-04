using GymTracker.Core.Text;
using GymTracker.Web.Features.Localization;
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Feedback;
using GymTracker.Web.Features.Food;
using GymTracker.Web.Features.Profile;
using GymTracker.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GymTracker.Web.Controllers;

[Route("food")]
public class FoodController(
    FoodLogService log,
    SavedMealService savedMeals,
    FoodItemService foods,
    ParseRunService parseRuns,
    NutritionTargets targets,
    ProfileService profiles,
    UserTime userTime,
    TimeProvider time,
    Localizer localizer) : Controller
{
    private static readonly string[] Tabs = ["text", "barcode", "search", "recent", "manual"];

    [HttpGet("")]
    public async Task<IActionResult> Index(DateOnly? date, CancellationToken ct)
    {
        var today = await profiles.TodayAsync(ct);
        var day = await log.DayAsync(date ?? today, ct);
        return View(new FoodDayViewModel(day, today, await targets.ForTodayAsync(ct)));
    }

    [HttpGet("add")]
    public async Task<IActionResult> Add(DateOnly? date, Meal? meal, string? tab, string? q, bool off, CancellationToken ct)
    {
        var d = date ?? await profiles.TodayAsync(ct);
        var m = meal ?? await DefaultMealAsync();
        tab = Tabs.Contains(tab) ? tab! : "text";

        var local = tab == "search" && !string.IsNullOrWhiteSpace(q) ? await foods.SearchAsync(q, 20, 0.3, ct) : [];
        var offResults = tab == "search" && off && !string.IsNullOrWhiteSpace(q) ? await foods.SearchOpenFoodFactsAsync(q, ct) : null;
        var recents = tab == "recent" ? await log.RecentsAsync(await profiles.TodayAsync(ct), ct) : [];
        var saved = tab == "recent" ? await savedMeals.ListAsync(ct) : [];
        return View(new FoodAddViewModel(d, m, tab, null, null, q, local, offResults, recents, saved));
    }

    [HttpPost("parse")]
    [EnableRateLimiting(RateLimits.AiParse)]
    public async Task<IActionResult> Parse(DateOnly date, Meal meal, string? text, CancellationToken ct)
    {
        text = text?.Trim() ?? "";
        if (text.Length is 0 or > 1000)
        {
            return View("Add", new FoodAddViewModel(date, meal, "text", text, "Write what you ate, up to 1000 characters.", null, [], null, [], []));
        }

        var (result, items, runId) = await parseRuns.ParseAsync(text, ct);
        if (items.Count == 0)
        {
            // Keep the text so nothing typed is lost, and offer manual entry.
            return View("Add", new FoodAddViewModel(date, meal, "text", text, result.Error ?? new LocalText("Couldn't read that."), null, [], null, [], []));
        }

        return View("Confirm", new ConfirmViewModel(date, meal, text, result.Model, items, result.Error, runId));
    }

    [HttpPost("confirm")]
    public async Task<IActionResult> Confirm(DateOnly date, Meal meal, string? aiModel, int? parseRunId, List<ConfirmRow> items, CancellationToken ct)
    {
        if (!items.Any(i => i.Include))
        {
            throw new ValidationException("Tick at least one food to save.");
        }

        var runId = await parseRuns.ConfirmAsync(parseRunId,
            items.Select(i => new SavedItem(i.Include, i.Description ?? "", i.Grams, new Per100g(i.Kcal, i.Protein, i.Carbs, i.Fat), i.FoodItemId)).ToList(), ct);
        var entries = items
            .Where(i => i.Include)
            .Select(i => new NewEntry(date, meal, i.Description ?? "", i.Grams, new Per100g(i.Kcal, i.Protein, i.Carbs, i.Fat), i.FoodItemId, EntrySource.Ai, aiModel, string.IsNullOrWhiteSpace(i.MatchModel) ? null : i.MatchModel.Trim(), runId))
            .ToList();
        await log.AddAsync(entries, ct);
        return Redirect($"/food?date={date:yyyy-MM-dd}");
    }

    [HttpGet("barcode")]
    public async Task<IActionResult> Barcode(DateOnly date, Meal meal, string? code, CancellationToken ct)
    {
        code = new string((code ?? "").Where(char.IsAsciiDigit).ToArray());
        return View(new BarcodeViewModel(date, meal, code, await foods.LookupBarcodeAsync(code, ct)));
    }

    [HttpPost("log-item")]
    public async Task<IActionResult> LogItem(DateOnly date, Meal meal, int foodItemId, decimal grams, EntrySource source, CancellationToken ct)
    {
        var item = await foods.GetAsync(foodItemId, ct);
        var name = item.DisplayName();
        var description = item.Brand is null ? name : $"{name} ({item.Brand})";
        await log.AddAsync([new NewEntry(date, meal, description, grams, Per100(item), item.Id, source)], ct);
        return Redirect($"/food?date={date:yyyy-MM-dd}");
    }

    [HttpPost("items")]
    public async Task<IActionResult> CreateItem(DateOnly date, Meal meal, string name, string? barcode, decimal kcal, decimal protein, decimal carbs, decimal fat, decimal? servingGrams, decimal grams, CancellationToken ct)
    {
        var item = await foods.CreateManualAsync(name, barcode, new Per100g(kcal, protein, carbs, fat), servingGrams, ct);
        await log.AddAsync([new NewEntry(date, meal, item.Name, grams, Per100(item), item.Id, barcode is null ? EntrySource.Manual : EntrySource.Barcode)], ct);
        return Redirect($"/food?date={date:yyyy-MM-dd}");
    }

    [HttpPost("log-recent")]
    public async Task<IActionResult> LogRecent(DateOnly date, Meal meal, string description, int? foodItemId, decimal grams, decimal kcal, decimal protein, decimal carbs, decimal fat, CancellationToken ct)
    {
        await log.AddAsync([new NewEntry(date, meal, description, grams, new Per100g(kcal, protein, carbs, fat), foodItemId, EntrySource.Recent)], ct);
        return Redirect($"/food?date={date:yyyy-MM-dd}");
    }

    [HttpPost("copy")]
    public async Task<IActionResult> Copy(DateOnly date, Meal meal, CancellationToken ct)
    {
        var copied = await log.CopyMealAsync(date.AddDays(-1), date, meal, ct);
        TempData.Notice(copied == 0
            ? new LocalText(NothingToCopy[meal])
            : LocalText.Count(copied, "Copied 1 item.", "Copied {0} items."));
        return Redirect($"/food?date={date:yyyy-MM-dd}");
    }

    // One sentence per meal: Bulgarian meal names change form inside a sentence.
    private static readonly Dictionary<Meal, string> NothingToCopy = new()
    {
        [Meal.Breakfast] = "Nothing was logged for breakfast the day before.",
        [Meal.Lunch] = "Nothing was logged for lunch the day before.",
        [Meal.Dinner] = "Nothing was logged for dinner the day before.",
        [Meal.Snack] = "No snacks were logged the day before.",
    };

    [HttpGet("quick")]
    public IActionResult QuickForm(DateOnly date, Meal meal) => PartialView("_QuickAddForm", new QuickAddViewModel(date, meal));

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

    [HttpGet("entries/{id:int}")]
    public async Task<IActionResult> Entry(int id, CancellationToken ct) => View(await log.GetAsync(id, ct));

    [HttpPost("entries/{id:int}")]
    public async Task<IActionResult> UpdateEntry(int id, string description, decimal grams, Meal meal, CancellationToken ct)
    {
        await log.UpdateAsync(id, description, grams, meal, ct);
        var entry = await log.GetAsync(id, ct);
        return Redirect($"/food?date={entry.Date:yyyy-MM-dd}");
    }

    [HttpPost("entries/{id:int}/delete")]
    public async Task<IActionResult> DeleteEntry(int id, CancellationToken ct)
    {
        var date = await log.DeleteAsync(id, ct);
        TempData.Undo("Entry deleted.", $"/undo/entry/{id}");
        return Redirect($"/food?date={date:yyyy-MM-dd}");
    }

    private static Per100g Per100(FoodItem f) => new(f.KcalPer100g, f.ProteinPer100g, f.CarbsPer100g, f.FatPer100g);

    /// <summary>The meal that fits the user's local time of day.</summary>
    private async Task<Meal> DefaultMealAsync()
    {
        var hour = (await userTime.LocalAsync(time.GetUtcNow())).Hour;
        return hour switch
        {
            < 4 => Meal.Snack,
            < 11 => Meal.Breakfast,
            < 16 => Meal.Lunch,
            < 22 => Meal.Dinner,
            _ => Meal.Snack,
        };
    }

    public class ConfirmRow
    {
        public bool Include { get; set; }
        public string? Description { get; set; }
        public decimal Grams { get; set; }
        public decimal Kcal { get; set; }
        public decimal Protein { get; set; }
        public decimal Carbs { get; set; }
        public decimal Fat { get; set; }
        public int? FoodItemId { get; set; }
        public string? MatchModel { get; set; }
    }
}
