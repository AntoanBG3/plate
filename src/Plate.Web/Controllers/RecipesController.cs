using Plate.Core.Domain;
using Plate.Web.Features;
using Plate.Web.Features.Food;
using Plate.Web.Features.Localization;
using Plate.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Plate.Web.Controllers;

/// <summary>The recipe editor (SPEC 5.12, 7 Food). Recipes are logged like any other food, from search or Recent.</summary>
[Route("food/recipes")]
public class RecipesController(RecipeService recipes, FoodItemService foods) : Controller
{
    [HttpGet("new")]
    public IActionResult New(DateOnly date, Meal meal) =>
        View("Recipe", new RecipeEditorModel(date, meal, new RecipeDraft(null, "", null, [])));

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Edit(int id, DateOnly date, Meal meal, CancellationToken ct) =>
        View("Recipe", new RecipeEditorModel(date, meal, await recipes.GetAsync(id, ct)));

    /// <summary>Ingredients typed as text become rows, through the parser and matcher; the same limit as a meal's parse.</summary>
    [HttpPost("parse")]
    [EnableRateLimiting(RateLimits.AiParse)]
    public async Task<IActionResult> Parse(string? text, CancellationToken ct)
    {
        text = text?.Trim() ?? "";
        if (text.Length is 0 or > 1000)
        {
            throw new ValidationException("Write the ingredients, up to 1000 characters.");
        }

        var (items, error) = await recipes.ParseIngredientsAsync(text, ct);
        if (items.Count == 0)
        {
            throw new ValidationException(error ?? "Couldn't read that.");
        }

        return PartialView("_RecipeRows", items);
    }

    /// <summary>Foods to add as ingredients; recipes are left out, since one can't be an ingredient of another.</summary>
    [HttpGet("search")]
    public async Task<IActionResult> Search(string? q, CancellationToken ct)
    {
        var found = string.IsNullOrWhiteSpace(q) ? [] : await foods.SearchForPickerAsync(q, 20, ct);
        var recipeIds = await foods.RecipeIdsAsync(found.Select(f => f.Id), ct);
        return PartialView("_RecipeSearch", found.Where(f => !recipeIds.Contains(f.Id)).Take(12).ToList());
    }

    [HttpPost("ingredient")]
    public async Task<IActionResult> Ingredient(int foodItemId, CancellationToken ct)
    {
        var food = await foods.GetAsync(foodItemId, ct);
        return PartialView("_RecipeRows", new List<ConfirmItem> { await recipes.IngredientAsync(foodItemId, food.DefaultServingGrams ?? 100, ct) });
    }

    [HttpPost("")]
    [HandlesInvalidNumbers]
    public Task<IActionResult> Create(DateOnly date, Meal meal, string? name, decimal? yieldGrams, List<FoodController.ConfirmRow> items, CancellationToken ct) =>
        SaveAsync(null, date, meal, name, yieldGrams, items, ct);

    [HttpPost("{id:int}")]
    [HandlesInvalidNumbers]
    public Task<IActionResult> Update(int id, DateOnly date, Meal meal, string? name, decimal? yieldGrams, List<FoodController.ConfirmRow> items, CancellationToken ct) =>
        SaveAsync(id, date, meal, name, yieldGrams, items, ct);

    [HttpPost("{id:int}/delete")]
    public async Task<IActionResult> Delete(int id, DateOnly date, Meal meal, CancellationToken ct)
    {
        await recipes.DeleteAsync(id, ct);
        TempData.Notice("Recipe deleted.");
        return Redirect($"/food/add?date={date:yyyy-MM-dd}&meal={meal}&tab=recent");
    }

    private async Task<IActionResult> SaveAsync(int? id, DateOnly date, Meal meal, string? name, decimal? yieldGrams, List<FoodController.ConfirmRow> items, CancellationToken ct)
    {
        var included = items.Where(i => i.Include).ToList();
        try
        {
            // A number that didn't read ("abc", a lone ".") would otherwise save as nothing or 0: the editor comes back.
            if (ModelState.Values.Any(field => field.Errors.Any(e => e.ErrorMessage == DecimalModelBinder.InvalidNumber)))
            {
                throw new ValidationException(DecimalModelBinder.InvalidNumber);
            }

            await recipes.SaveAsync(id, name, yieldGrams,
                included.Select(i => new RecipeIngredientInput(i.Description ?? "", i.Grams, i.FoodItemId, i.Per100)).ToList(), ct);
        }
        catch (ValidationException ex) when (!Request.Headers.ContainsKey("HX-Request"))
        {
            // The editor comes back as it was posted, ticked rows only, with what to fix.
            var rows = included.Select(i => new ConfirmItem(i.Description ?? "", i.Grams, i.Per100, i.FoodItemId,
                i.FoodItemId is null ? null : i.Description, i.FoodItemId is null ? Confidence.Low : Confidence.High, [])).ToList();
            return View("Recipe", new RecipeEditorModel(date, meal, new RecipeDraft(id, name ?? "", yieldGrams, rows), ex.Text));
        }

        TempData.Notice("Recipe saved.");
        return Redirect($"/food/add?date={date:yyyy-MM-dd}&meal={meal}&tab=recent");
    }
}
