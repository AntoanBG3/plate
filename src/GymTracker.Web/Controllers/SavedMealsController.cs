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
        PartialView("~/Views/Food/_SaveMealForm.cshtml", new SaveMealFormViewModel(date, meal, localizer.Enum(meal)));

    [HttpPost("")]
    public async Task<IActionResult> Create(DateOnly date, Meal meal, string name, CancellationToken ct)
    {
        await saved.SaveFromMealAsync(date, meal, name, ct);
        TempData.Notice("Saved as a meal.");
        TempData.Focus($"meal-{meal.ToString().ToLowerInvariant()}");
        return Redirect($"/food?date={date:yyyy-MM-dd}");
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Show(int id, DateOnly date, Meal meal, CancellationToken ct) =>
        PartialView("~/Views/Food/_SavedMeal.cshtml", new SavedMealViewModel(await saved.GetAsync(id, ct), date, meal));

    [HttpPost("{id:int}/log")]
    [IdempotentFoodPost]
    public async Task<IActionResult> Log(int id, DateOnly date, Meal meal, List<decimal?> grams, CancellationToken ct)
    {
        var count = await saved.LogAsync(id, date, meal, grams, ct);
        TempData.Notice(LocalText.Count(count, "Added 1 item.", "Added {0} items."));
        TempData.Focus($"meal-{meal.ToString().ToLowerInvariant()}");
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
