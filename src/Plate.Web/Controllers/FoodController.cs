using Plate.Core.Text;
using Plate.Web.Features.Localization;
using Plate.Core.Domain;
using Plate.Core.Nutrition;
using Plate.Web.Features;
using Plate.Web.Features.Feedback;
using Plate.Web.Features.Food;
using Plate.Web.Features.Profile;
using Plate.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Plate.Web.Controllers;

[Route("food")]
public class FoodController(
    FoodLogService log,
    SavedMealService savedMeals,
    FoodItemService foods,
    RepeatLoggingService repeat,
    LabelScanService labels,
    ManualFoodService manualFoods,
    ParseRunService parseRuns,
    NutritionTargets targets,
    ProfileService profiles,
    UserTime userTime,
    Plate.Web.Data.ICurrentUser currentUser,
    TimeProvider time) : Controller
{
    private static readonly string[] Tabs = ["text", "barcode", "search", "recent", "manual"];

    /// <summary>Which of the review's foods count in ml, read when the page is shown (SPEC 4).</summary>
    private async Task<ConfirmViewModel> WithUnitsAsync(ConfirmViewModel draft, CancellationToken ct) =>
        draft with { MillilitreFoods = await foods.MillilitreFoodsAsync(draft.Items.Select(i => i.FoodItemId).OfType<int>(), ct) };

    [HttpGet("")]
    public async Task<IActionResult> Index(DateOnly? date, CancellationToken ct)
    {
        var today = await profiles.TodayAsync(ct);
        if (date is { } asked && !FoodDays.IsOpenable(asked, today))
        {
            return Redirect("/food"); // future days and ancient ones can't be opened (SPEC 7)
        }

        var shown = date ?? today;
        var day = await log.DayAsync(shown, ct);
        DailyTargets? dayTargets;
        DateOnly? start = null;
        if (shown == today)
        {
            dayTargets = await targets.ForTodayAsync(ct);
        }
        else
        {
            (dayTargets, start) = await targets.ForDateAsync(shown, ct);
        }

        var week = shown == today && dayTargets is not null ? await log.WeekCardAsync(today, dayTargets, ct) : null;
        var previous = (await log.DayAsync(shown.AddDays(-1), ct)).Entries.Select(e => e.Meal).ToHashSet();
        return View(new FoodDayViewModel(day, today, dayTargets, week, start, previous));
    }

    [HttpGet("add")]
    public async Task<IActionResult> Add(DateOnly? date, Meal? meal, string? tab, string? q, bool off, CancellationToken ct)
    {
        var today = await profiles.TodayAsync(ct);
        if (date is { } asked && !FoodDays.IsOpenable(asked, today))
        {
            return Redirect("/food");
        }

        var d = date ?? today;
        var m = meal ?? await DefaultMealAsync();
        tab = Tabs.Contains(tab) ? tab! : "text";

        var local = tab == "search" && !string.IsNullOrWhiteSpace(q) ? await foods.SearchForPickerAsync(q, 20, ct) : [];
        var offResults = tab == "search" && off && !string.IsNullOrWhiteSpace(q) ? await foods.SearchOpenFoodFactsAsync(q, ct) : null;
        var recents = tab == "recent" ? await log.RecentsAsync(today, m, ct) : [];
        var saved = tab == "recent" ? await savedMeals.ListAsync(ct) : [];
        var labelAvailable = tab is "manual" or "barcode" && await labels.IsAvailableAsync(ct);
        var lastGrams = local.Count > 0 ? await repeat.LastAmountsAsync(local.Select(f => f.Id).ToList(), ct) : null;
        return View(new FoodAddViewModel(d, m, tab, null, null, q, local, offResults, recents, saved, labelAvailable, lastGrams));
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

        var draft = await parseRuns.CreateReviewAsync(date, meal, text, ct);
        if (draft.Items.Count == 0)
        {
            return View("Add", new FoodAddViewModel(date, meal, "text", text, draft.Warning ?? new LocalText("Couldn't read that."), null, [], null, [], []));
        }

        var url = Url.Action(nameof(Review), new { id = draft.RequestId, run = draft.ParseRunId })!;
        if (Request.Headers["HX-Request"] == "true")
        {
            Response.Headers["HX-Push-Url"] = url;
            return View("Confirm", await WithUnitsAsync(draft, ct));
        }
        return Redirect(url);
    }

    [HttpGet("review/{id:guid}")]
    public async Task<IActionResult> Review(Guid id, int? run, CancellationToken ct)
    {
        if (await parseRuns.SavedLocationAsync(id, ct) is { } saved)
        {
            TempData.SavedReview(id, alreadySaved: true);
            return LocalRedirect(saved);
        }
        var draft = await parseRuns.ReviewAsync(run, id, ct);
        return draft is null ? NotFound() : View("Confirm", await WithUnitsAsync(draft, ct));
    }

    [HttpPost("confirm")]
    [IdempotentFoodPost(review: true)]
    public async Task<IActionResult> Confirm(DateOnly date, Meal meal, string? aiModel, int? parseRunId, List<ConfirmRow> items, CancellationToken ct)
    {
        if (!items.Any(i => i.Include))
        {
            throw new ValidationException("Tick at least one food to save.");
        }

        var saved = items.Select(i => new SavedItem(i.Include, i.Description ?? "", i.Grams, i.Per100, i.FoodItemId)).ToList();
        var runId = await parseRuns.ConfirmAsync(parseRunId, saved, ct);
        var entries = items
            .Where(i => i.Include)
            .Select(i => new NewEntry(date, meal, i.Description ?? "", i.Grams, i.Per100, i.FoodItemId, EntrySource.Ai, aiModel, string.IsNullOrWhiteSpace(i.MatchModel) ? null : i.MatchModel.Trim(), runId, i.Status))
            .ToList();
        await log.AddAsync(entries, ct);
        await parseRuns.RememberAsync(runId, saved, ct);
        FocusMeal(meal);
        return Redirect($"/food?date={date:yyyy-MM-dd}");
    }

    /// <summary>Foods to put on a confirmation row instead of the one it matched (the Change food sheet).</summary>
    [HttpGet("candidates")]
    public async Task<IActionResult> Candidates(string? q, CancellationToken ct) =>
        PartialView("_Candidates", string.IsNullOrWhiteSpace(q) ? [] : await foods.SearchForPickerAsync(q, 20, ct));

    [HttpGet("barcode")]
    public async Task<IActionResult> Barcode(DateOnly date, Meal meal, string? code, CancellationToken ct)
    {
        var digits = Barcodes.Normalize(code);
        if (!Barcodes.IsValid(digits))
        {
            // Back to the Scan tab with what was typed, so one wrong digit can be fixed (Phase 11).
            return View("Add", new FoodAddViewModel(date, meal, "barcode", new string((code ?? "").Where(char.IsAsciiDigit).ToArray()),
                "That isn't a valid EAN or UPC barcode.", null, [], null, [], []));
        }

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
    }

    /// <summary>The food the app already has for a barcode typed or scanned into the manual form (SPEC 7). Local only: no Open Food Facts call.</summary>
    [HttpGet("barcode/known")]
    public async Task<IActionResult> KnownBarcode(DateOnly date, Meal meal, string? code, CancellationToken ct)
    {
        var digits = Barcodes.Normalize(code);
        if (!Barcodes.IsValid(digits) || await foods.FindByBarcodeAsync(digits, ct) is not { } item)
        {
            return NoContent();
        }

        return PartialView("_KnownBarcode", new KnownBarcodeModel(date, meal, digits, item,
            Own: item.OwnerUserId is not null && item.OwnerUserId == currentUser.UserId,
            AddedByAnother: FoodItemService.AddedByAnother(item, currentUser.UserId),
            SharedByYou: item.OwnerUserId is null && item.AddedByUserId is not null && item.AddedByUserId == currentUser.UserId));
    }

    [HttpPost("log-item")]
    [IdempotentFoodPost]
    public async Task<IActionResult> LogItem(DateOnly date, Meal meal, int foodItemId, decimal grams, EntrySource source, string? name, string? next, CancellationToken ct)
    {
        var item = await foods.GetAsync(foodItemId, ct);
        if (item.Unnamed && name is not null)
        {
            item = await foods.NameUnnamedAsync(item, name, ct);
        }

        var description = FoodItemService.EntryDescription(item.DisplayName(), item.Brand);
        await log.AddAsync([new NewEntry(date, meal, description, grams, item.Per100(), item.Id, source)], ct);
        if (next == "scan" && source == EntrySource.Barcode)
        {
            // The Scan tab for the same day and meal; the camera starts on the user's tap, as always (SPEC 7).
            TempData.Notice(new LocalText("Added: {0}", description));
            return Redirect($"/food/add?date={date:yyyy-MM-dd}&meal={meal}&tab=barcode");
        }

        FocusMeal(meal);
        return Redirect($"/food?date={date:yyyy-MM-dd}");
    }

    [HttpPost("items")]
    [IdempotentFoodPost]
    [HandlesInvalidNumbers]
    public async Task<IActionResult> CreateItem(DateOnly date, Meal meal, string? name, string? barcode, decimal kcal, decimal protein, decimal carbs, decimal fat, decimal? fiber, decimal? saturatedFat, decimal? servingGrams, decimal grams, bool share, int? labelScanId, bool barcodeField, CancellationToken ct, FoodUnit unit = FoodUnit.Grams)
    {
        // Hidden routing and scan fields cannot be corrected in the form. Never reuse their binding defaults.
        if (ModelState.Values.Any(field => field.Errors.Any(e => e.ErrorMessage != DecimalModelBinder.InvalidNumber)))
            throw new ValidationException("This food form is out of date. Check whether it was saved, then reload before trying again.");
        try
        {
            if (ModelState.FirstOrDefault(pair => pair.Value?.Errors.Count > 0).Key is { } field)
                throw new ValidationException(DecimalModelBinder.InvalidNumber, field);

            await manualFoods.SaveAndLogAsync(date, meal, name, barcode, new Per100g(kcal, protein, carbs, fat, fiber, saturatedFat), servingGrams, grams, share, labelScanId, ct, unit);
        }
        catch (ValidationException ex) when (!Request.Headers.ContainsKey("HX-Request"))
        {
            var available = await labels.IsAvailableAsync(ct);
            var manual = new ManualFoodFormModel(date, meal, barcodeField ? null : barcode, name, barcodeField,
                LabelScanId: labelScanId, LabelAvailable: available, BarcodeValue: barcode, Share: share,
                InvalidField: ex.Field, Submitted: true, Unit: unit);
            if (!barcodeField && barcode is not null) ViewData["Page"] = "food-barcode";
            return View("Add", new FoodAddViewModel(date, meal, "manual", null, ex.Text, null, [], null, [], [],
                available, Manual: manual));
        }
        FocusMeal(meal);
        return Redirect($"/food?date={date:yyyy-MM-dd}");
    }

    /// <summary>Reads one to three label photos and answers with the manual food form filled from them (SPEC 6.6).</summary>
    [HttpPost("label")]
    [EnableRateLimiting(RateLimits.AiLabel)]
    // The one endpoint above the 1 MB request limit (SPEC 9): up to three photos the phone shrank to under 2 MB each.
    [RequestSizeLimit(7 * 1024 * 1024)]
    public async Task<IActionResult> Label(DateOnly date, Meal meal, string? barcode, bool barcodeField, string? name, bool? share, decimal? grams, decimal? servingGrams,
        List<int> width, List<int> height, List<IFormFile> photo, CancellationToken ct)
    {
        if (!await labels.IsAvailableAsync(ct))
        {
            return NotFound();
        }

        if (photo.Count == 0 || photo.Any(p => p.Length == 0))
        {
            throw new ValidationException("Attach a photo (JPEG, PNG or WebP).");
        }

        if (photo.Count > LabelScanPhoto.MaxPerScan)
        {
            throw new ValidationException("Attach one to three photos.");
        }

        if (photo.Any(p => p.Length > LabelScanPhoto.MaxBytes))
        {
            throw new ValidationException("That photo is too large.");
        }

        var uploads = new List<LabelPhotoUpload>(photo.Count);
        for (var i = 0; i < photo.Count; i++)
        {
            using var buffer = new MemoryStream((int)photo[i].Length);
            await photo[i].CopyToAsync(buffer, ct);
            uploads.Add(new LabelPhotoUpload(buffer.ToArray(), width.ElementAtOrDefault(i), height.ElementAtOrDefault(i)));
        }

        var result = await labels.ReadAsync(uploads, ct);
        if (result.Values is null)
        {
            // Only the message goes back: the form the user already filled in stays as it is (SPEC 6.6).
            throw new ValidationException(result.Error ?? "Couldn't read the label. Enter the values by hand.");
        }

        barcode = string.IsNullOrWhiteSpace(barcode) ? null : barcode.Trim();
        return PartialView("_ManualFoodForm", new ManualFoodFormModel(date, meal, barcodeField ? null : barcode, string.IsNullOrWhiteSpace(name) ? null : name.Trim(), barcodeField,
            Label: result.Values, LabelScanId: result.ScanId, LabelAvailable: true,
            BarcodeValue: barcodeField ? barcode : null, Share: share ?? true, TypedGrams: grams, TypedServing: servingGrams));
    }

    [HttpPost("log-recent")]
    [IdempotentFoodPost]
    public async Task<IActionResult> LogRecent(DateOnly date, Meal meal, string description, int? foodItemId, decimal grams, decimal kcal, decimal protein, decimal carbs, decimal fat, decimal? fiber, decimal? saturatedFat, CancellationToken ct)
    {
        await log.AddAsync([new NewEntry(date, meal, description, grams, new Per100g(kcal, protein, carbs, fat, fiber, saturatedFat), foodItemId, EntrySource.Recent)], ct);
        FocusMeal(meal);
        return Redirect($"/food?date={date:yyyy-MM-dd}");
    }

    [HttpPost("copy")]
    [IdempotentFoodPost]
    public async Task<IActionResult> Copy(DateOnly date, Meal meal, CancellationToken ct)
    {
        var copied = await log.CopyMealAsync(date.AddDays(-1), date, meal, ct);
        if (copied == 0)
        {
            TempData.Info(new LocalText(NothingToCopy[meal]));
        }
        else
        {
            TempData.Notice(LocalText.Count(copied, "Copied 1 item.", "Copied {0} items."));
        }
        FocusMeal(meal);
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
    [IdempotentFoodPost]
    public async Task<IActionResult> Quick(DateOnly date, Meal meal, string? description, decimal kcal, decimal? protein, decimal? carbs, decimal? fat, decimal? fiber, decimal? saturatedFat, CancellationToken ct)
    {
        // Left empty, the name is shown as "Quick add" in whichever language is reading it (Localizer.Entry).
        await log.AddQuickAsync(date, meal, description ?? "", kcal, protein ?? 0, carbs ?? 0, fat ?? 0, fiber, saturatedFat, ct);
        FocusMeal(meal);
        return Redirect($"/food?date={date:yyyy-MM-dd}");
    }

    [HttpPost("entries/{id:int}/quick")]
    public async Task<IActionResult> UpdateQuick(int id, string? description, Meal meal, decimal kcal, decimal? protein, decimal? carbs, decimal? fat, decimal? fiber, decimal? saturatedFat, CancellationToken ct)
    {
        await log.UpdateQuickAsync(id, description ?? "", meal, kcal, protein ?? 0, carbs ?? 0, fat ?? 0, fiber, saturatedFat, ct);
        var entry = await log.GetAsync(id, ct);
        FocusMeal(entry.Meal);
        return Redirect($"/food?date={entry.Date:yyyy-MM-dd}");
    }

    [HttpGet("entries/{id:int}")]
    public async Task<IActionResult> Entry(int id, CancellationToken ct)
    {
        var entry = await log.GetAsync(id, ct);
        if (entry.FoodItemId is { } foodId)
        {
            // Another user's private food stays hidden: the name is then left out rather than shown.
            if (await foods.FindVisibleAsync(foodId, ct) is { } food)
            {
                ViewData["MatchedName"] = Plate.Web.Features.Localization.FoodNames.DisplayName(food);
                ViewData["FoodState"] = FoodStates.FromName(food.Name, food.NameBg);
                ViewData["AddedByAnother"] = FoodItemService.AddedByAnother(food, currentUser.UserId);
            }
        }

        return View(entry);
    }

    [HttpPost("entries/{id:int}")]
    public async Task<IActionResult> UpdateEntry(int id, string description, decimal grams, Meal meal, CancellationToken ct)
    {
        await log.UpdateAsync(id, description, grams, meal, ct);
        var entry = await log.GetAsync(id, ct);
        FocusMeal(entry.Meal);
        return Redirect($"/food?date={entry.Date:yyyy-MM-dd}");
    }

    [HttpPost("entries/{id:int}/delete")]
    public async Task<IActionResult> DeleteEntry(int id, CancellationToken ct)
    {
        var meal = (await log.GetAsync(id, ct)).Meal;
        var date = await log.DeleteAsync(id, ct);
        TempData.Undo("Entry deleted.", $"/undo/entry/{id}");
        FocusMeal(meal);
        return Redirect($"/food?date={date:yyyy-MM-dd}");
    }

    /// <summary>The day view scrolls to the meal a change was made in (Phase 11).</summary>
    private void FocusMeal(Meal meal) => TempData.Focus($"meal-{meal.ToString().ToLowerInvariant()}");

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
        public decimal? Kcal { get; set; }
        public decimal? Protein { get; set; }
        public decimal? Carbs { get; set; }
        public decimal? Fat { get; set; }
        public decimal? Fiber { get; set; }
        public decimal? SaturatedFat { get; set; }
        public int? FoodItemId { get; set; }
        public string? MatchModel { get; set; }
        public string? Status { get; set; }

        public Per100g Per100 => new(Kcal ?? 0, Protein ?? 0, Carbs ?? 0, Fat ?? 0, Fiber, SaturatedFat);
    }
}
