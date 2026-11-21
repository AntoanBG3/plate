namespace Plate.Core.Nutrition;

/// <summary>One food of a weighed meal: the edible grams eaten (served minus leftovers) and per-100 g values from a label or table.</summary>
/// <param name="Uncertain">The weight or the values are a guess (a sauce that couldn't be weighed, a dish without its recipe).</param>
public record WeighedItem(string Name, decimal Grams, Per100g Per100, string? Source, bool Uncertain);

/// <summary>A meal from the weighed-meal pilot (reports/weighed-meal-pilot.md): what was typed into Plate, and what was weighed.</summary>
/// <param name="Category">counted, staple, homemade or loose (the pilot's four kinds of meal).</param>
public record WeighedMeal(string Id, DateTimeOffset? EatenAt, string? Account, string Category, string Text, IReadOnlyList<WeighedItem> Items)
{
    public MealNutrients Reference => MealNutrients.Of(Items.Select(i => (i.Grams, i.Per100)));

    public bool Uncertain => Items.Any(i => i.Uncertain);
}

/// <param name="Plate">What Plate's parser and matcher give for the meal's text.</param>
public record WeighedResult(WeighedMeal Meal, MealNutrients Plate)
{
    public decimal KcalError => Plate.Kcal - Meal.Reference.Kcal;
}

/// <param name="Overall">Over the meals without uncertain items.</param>
/// <param name="Uncertain">Meals left out of the summaries because an item is a guess.</param>
public record WeighedSummary(NutrientAgreement Overall, IReadOnlyDictionary<string, NutrientAgreement> ByCategory, IReadOnlyList<WeighedResult> LargestMisses, int Uncertain);

/// <summary>How far Plate is from weighed reference values (SPEC 11): absolute and signed kcal error per meal, macro errors.</summary>
public static class WeighedMeals
{
    public static WeighedSummary Summarize(IReadOnlyList<WeighedResult> results, int misses = 5)
    {
        var certain = results.Where(r => !r.Meal.Uncertain).ToList();
        NutrientAgreement Of(IEnumerable<WeighedResult> rs) => NutrientAgreement.Summarize(rs.Select(r => ((MealNutrients?)r.Meal.Reference, r.Plate)));
        return new WeighedSummary(
            Of(certain),
            certain.GroupBy(r => r.Meal.Category).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => Of(g)),
            certain.OrderByDescending(r => Math.Abs(r.KcalError)).Take(misses).ToList(),
            results.Count - certain.Count);
    }
}
