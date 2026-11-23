using Plate.Core.Domain;
using Plate.Core.Nutrition;
using Plate.Core.Text;
using Plate.Web.Data;
using Plate.Web.Features.Localization;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Features.Food;

/// <summary>An ingredient row as the recipe editor posts it: the Check foods row's fields.</summary>
public record RecipeIngredientInput(string Description, decimal Grams, int? FoodItemId, Per100g Per100);

/// <summary>A recipe on the Recent tab: its food, values per 100 g and how many ingredients it has.</summary>
public record RecipeSummary(int Id, string Name, Per100g Per100, int Ingredients);

/// <summary>A recipe opened for editing, its rows ready for the editor.</summary>
public record RecipeDraft(int? Id, string Name, decimal? YieldGrams, IReadOnlyList<ConfirmItem> Items);

/// <summary>
/// Home dishes built from their ingredients (SPEC 5.12). A recipe is a private food with
/// <see cref="FoodSource.Recipe"/>, so search, Recent and text parsing find it like any other of the account's foods.
/// </summary>
public class RecipeService(AppDbContext db, ICurrentUser currentUser, TimeProvider time, IFoodParser parser, FoodMatcher matcher, FoodItemService foods)
{
    private const string BeforeCookingNote = "Counted as weighed before cooking.";

    public Task<List<RecipeSummary>> ListAsync(CancellationToken ct) =>
        db.Recipes.AsNoTracking()
            .OrderBy(r => r.FoodItem.Name)
            .Select(r => new RecipeSummary(r.FoodItemId, r.FoodItem.Name,
                new Per100g(r.FoodItem.KcalPer100g, r.FoodItem.ProteinPer100g, r.FoodItem.CarbsPer100g, r.FoodItem.FatPer100g, r.FoodItem.FiberPer100g, r.FoodItem.SaturatedFatPer100g),
                r.Ingredients.Count))
            .ToListAsync(ct);

    /// <summary>A recipe of this account for the editor, or a 404 for anyone else's.</summary>
    public async Task<RecipeDraft> GetAsync(int id, CancellationToken ct)
    {
        var recipe = await db.Recipes.AsNoTracking().Include(r => r.FoodItem).Include(r => r.Ingredients)
            .SingleOrDefaultAsync(r => r.FoodItemId == id, ct) ?? throw new NotFoundException();
        var ids = recipe.Ingredients.Select(i => i.FoodItemId).OfType<int>().ToList();
        var foods = await db.FoodItems.AsNoTracking().Where(f => ids.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);
        var items = recipe.Ingredients.OrderBy(i => i.SortOrder).Select(i =>
        {
            var per100 = new Per100g(i.KcalPer100g, i.ProteinPer100g, i.CarbsPer100g, i.FatPer100g, i.FiberPer100g, i.SaturatedFatPer100g);
            return i.FoodItemId is { } foodId && foods.TryGetValue(foodId, out var food)
                ? new ConfirmItem(i.Description, i.Grams, per100, foodId, food.DisplayName(), Confidence.High, [], Own: food.OwnerUserId is not null)
                : new ConfirmItem(i.Description, i.Grams, per100, null, null, Confidence.Low, []);
        }).ToList();
        return new RecipeDraft(id, recipe.FoodItem.Name, recipe.YieldGrams, items);
    }

    /// <summary>
    /// Ingredients typed as text, through the parser and matcher onto editor rows. Nothing is recorded as a parse run:
    /// these are ingredients, not a meal (the run counts and model accuracy stay about meals). A weighed staple whose
    /// state the words don't give starts on the dry or raw food, since a recipe weighs what goes into the pot.
    /// A recipe can't be an ingredient, so a row matched to one falls back to the parser's estimate.
    /// </summary>
    public async Task<(IReadOnlyList<ConfirmItem> Items, LocalText? Error)> ParseIngredientsAsync(string text, CancellationToken ct)
    {
        var result = await parser.ParseAsync(text, ct);
        if (result.Items.Count == 0)
        {
            return ([], result.Error ?? "Couldn't read that.");
        }

        var rows = await matcher.ResolveAsync(text, result.Items, ct);
        var recipes = await foods.RecipeIdsAsync(rows.Select(r => r.FoodItemId).OfType<int>(), ct);
        var items = rows.Select((row, n) =>
        {
            if (row.FoodItemId is { } id && recipes.Contains(id))
            {
                var parsed = result.Items[n];
                return new ConfirmItem(FoodText.Capitalize(parsed.NameOriginal), row.Grams, parsed.Estimated, null, null, Confidence.Low, [], Typed: row.Typed);
            }

            if (row is { Choice: { Current: FoodState.Cooked, Other: { State: FoodState.Raw } dry } choice, FoodItemId: { } cookedId })
            {
                return row with
                {
                    Description = dry.Name,
                    MatchedName = dry.Name,
                    FoodItemId = dry.Id,
                    Per100 = dry.Per100,
                    State = FoodState.Raw,
                    Status = null,
                    Choice = new StateChoice(FoodState.Raw, new ChoiceFood(cookedId, row.MatchedName ?? row.Description, row.Per100, FoodState.Cooked)),
                    Notes = [.. row.Notes, new LocalText(BeforeCookingNote)],
                };
            }

            return row;
        }).ToList();
        return (items, result.Error);
    }

    /// <summary>A food picked from search as an ingredient row; a recipe can't be one.</summary>
    public async Task<ConfirmItem> IngredientAsync(int foodItemId, decimal grams, CancellationToken ct)
    {
        var food = await db.FoodItems.AsNoTracking().SingleOrDefaultAsync(f => f.Id == foodItemId, ct) ?? throw new NotFoundException("Food not found.");
        if (food.Source == FoodSource.Recipe)
        {
            throw new ValidationException("A recipe can't be an ingredient of another recipe.");
        }

        return new ConfirmItem(food.DisplayName(), grams, food.Per100(), food.Id, food.DisplayName(), Confidence.High, [], Own: food.OwnerUserId is not null);
    }

    /// <summary>
    /// Saves a new recipe (<paramref name="id"/> null) or replaces one's name, weight and ingredients. An ingredient
    /// that is a database food takes that food's values, never the posted ones; only a model's estimate is taken as
    /// posted. Entries already logged keep their totals.
    /// </summary>
    public async Task<FoodItem> SaveAsync(int? id, string? name, decimal? yieldGrams, IReadOnlyList<RecipeIngredientInput> inputs, CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new InvalidOperationException("No signed-in user.");
        name = name?.Trim() ?? "";
        if (name.Length is 0 or > RecipeMath.MaxNameLength)
        {
            throw new ValidationException(new LocalText("Give the recipe a name of up to {0} characters.", RecipeMath.MaxNameLength), nameof(name));
        }

        var lower = name.ToLowerInvariant();
        if (await db.FoodItems.AnyAsync(f => f.Source == FoodSource.Recipe && f.OwnerUserId == userId && f.Id != id && f.Name.ToLower() == lower, ct))
        {
            throw new ValidationException(new LocalText("You already have a recipe called {0}.", name), nameof(name));
        }

        Recipe? recipe = null;
        if (id is { } existingId)
        {
            recipe = await db.Recipes.Include(r => r.FoodItem).Include(r => r.Ingredients).SingleOrDefaultAsync(r => r.FoodItemId == existingId, ct)
                ?? throw new NotFoundException();
        }

        var ids = inputs.Select(i => i.FoodItemId).OfType<int>().Distinct().ToList();
        var foods = await db.FoodItems.AsNoTracking().Where(f => ids.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);
        var ingredients = new List<RecipeIngredient>();
        foreach (var (input, n) in inputs.Select((x, n) => (x, n)))
        {
            var description = input.Description.Trim();
            if (description.Length is 0 or > 200)
            {
                throw new ValidationException("Each ingredient needs a name of up to 200 characters.");
            }

            var per100 = input.Per100;
            if (input.FoodItemId is { } foodId)
            {
                if (!foods.TryGetValue(foodId, out var food))
                {
                    throw new NotFoundException("Food not found.");
                }

                if (food.Source == FoodSource.Recipe)
                {
                    throw new ValidationException("A recipe can't be an ingredient of another recipe.");
                }

                per100 = food.Per100();
            }

            ingredients.Add(new RecipeIngredient
            {
                SortOrder = n,
                Description = description,
                Grams = Math.Round(input.Grams, 1),
                FoodItemId = input.FoodItemId,
                KcalPer100g = per100.Kcal,
                ProteinPer100g = per100.Protein,
                CarbsPer100g = per100.Carbs,
                FatPer100g = per100.Fat,
                FiberPer100g = per100.Fiber,
                SaturatedFatPer100g = per100.SaturatedFat,
            });
        }

        var parts = ingredients.Select(i => new RecipePart(i.Grams,
            new Per100g(i.KcalPer100g, i.ProteinPer100g, i.CarbsPer100g, i.FatPer100g, i.FiberPer100g, i.SaturatedFatPer100g))).ToList();
        var cooked = yieldGrams is > 0 ? Math.Round(yieldGrams.Value, 1) : (decimal?)null;
        if (RecipeMath.Validate(parts, cooked) is { } error)
        {
            throw new ValidationException(error);
        }

        var values = RecipeMath.Calculate(parts, cooked).Per100;
        var item = recipe?.FoodItem ?? new FoodItem { OwnerUserId = userId, AddedByUserId = userId, Source = FoodSource.Recipe };
        item.Name = name;
        (item.KcalPer100g, item.ProteinPer100g, item.CarbsPer100g, item.FatPer100g) = (values.Kcal, values.Protein, values.Carbs, values.Fat);
        (item.FiberPer100g, item.SaturatedFatPer100g) = (values.Fiber, values.SaturatedFat);
        if (recipe is null)
        {
            recipe = new Recipe { FoodItem = item };
            db.Recipes.Add(recipe);
        }
        else
        {
            db.RecipeIngredients.RemoveRange(recipe.Ingredients);
        }

        recipe.YieldGrams = cooked;
        recipe.UpdatedAt = time.GetUtcNow();
        recipe.Ingredients = ingredients;
        await db.SaveChangesAsync(ct);
        return item;
    }

    /// <summary>
    /// Deletes a recipe and its food. Entries logged from it keep their name and totals; their link to the food is
    /// cleared by the foreign key, as for any deleted food.
    /// </summary>
    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        var recipe = await db.Recipes.Include(r => r.FoodItem).SingleOrDefaultAsync(r => r.FoodItemId == id, ct) ?? throw new NotFoundException();
        db.FoodItems.Remove(recipe.FoodItem);
        await db.SaveChangesAsync(ct);
    }
}
