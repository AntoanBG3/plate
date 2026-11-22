using Plate.Core.Text;

namespace Plate.Core.Nutrition;

/// <summary>One ingredient as weighed into a recipe: its amount and the food's values per 100 g.</summary>
public record RecipePart(decimal Grams, Per100g Per100);

/// <summary>A recipe's values: per 100 g of the dish, and the weights they were worked out from.</summary>
/// <param name="RawGrams">The ingredients' weights added up.</param>
/// <param name="WeightGrams">What the dish weighs: the cooked weight when one was given, otherwise the raw weight.</param>
public record RecipeValues(Per100g Per100, decimal RawGrams, decimal WeightGrams, Totals Totals);

/// <summary>
/// A home dish built once from its ingredients and logged by weight like any food (SPEC 5.12). Totals are the sum of
/// grams × per-100 g ÷ 100; per 100 g is totals × 100 ÷ the dish's weight, which is the cooked weight when given
/// (cooking loses water, so the same dish is denser) and otherwise the ingredients' raw weight.
/// </summary>
public static class RecipeMath
{
    public const int MaxIngredients = 30;
    public const int MaxNameLength = 60;

    /// <summary>A cooked weight from 20% to 300% of the raw weight: a reduction can concentrate, rice or pasta can triple.</summary>
    public const decimal MinYieldShare = 0.2m;
    public const decimal MaxYieldShare = 3m;

    /// <summary>Fiber and saturated fat are only known when the ingredients that have them carry this share of the energy.</summary>
    public const decimal KnownShare = 0.95m;

    /// <summary>Returns an error, or null when the recipe can be saved.</summary>
    public static LocalText? Validate(IReadOnlyList<RecipePart> parts, decimal? yieldGrams)
    {
        if (parts.Count == 0)
        {
            return "Add at least one ingredient.";
        }

        if (parts.Count > MaxIngredients)
        {
            return new LocalText("A recipe can have up to {0} ingredients.", MaxIngredients);
        }

        foreach (var part in parts)
        {
            if (FoodMath.Validate(part.Grams, part.Per100) is { } error)
            {
                return error;
            }
        }

        var raw = parts.Sum(p => p.Grams);
        if (yieldGrams is { } cooked && (cooked < raw * MinYieldShare || cooked > raw * MaxYieldShare))
        {
            return new LocalText("The cooked weight should be between {0} and {1} g for these ingredients.",
                Math.Ceiling(raw * MinYieldShare).ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                Math.Floor(raw * MaxYieldShare).ToString("0", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (FoodMath.Validate(100, Calculate(parts, yieldGrams).Per100) is not null)
        {
            return "With this cooked weight the dish would have impossible values per 100 g. Check the weights.";
        }

        return null;
    }

    /// <summary>The dish's values; call <see cref="Validate"/> first. Rounded like a label (6.6): kcal whole, the rest to 0.1.</summary>
    public static RecipeValues Calculate(IReadOnlyList<RecipePart> parts, decimal? yieldGrams)
    {
        var raw = parts.Sum(p => p.Grams);
        var weight = yieldGrams ?? raw;
        var each = parts.Select(p => (Part: p, Totals: Exact(p.Per100, p.Grams))).ToList();
        var kcal = each.Sum(x => x.Totals.Kcal);

        decimal? Known(Func<Per100g, decimal?> value)
        {
            var with = each.Where(x => value(x.Part.Per100) is not null).ToList();
            var covered = kcal > 0 ? with.Sum(x => x.Totals.Kcal) / kcal : with.Count == each.Count ? 1 : 0;
            return covered >= KnownShare ? with.Sum(x => value(x.Part.Per100)!.Value * x.Part.Grams / 100) : null;
        }

        var totals = new Totals(kcal, each.Sum(x => x.Totals.ProteinG), each.Sum(x => x.Totals.CarbsG), each.Sum(x => x.Totals.FatG),
            Known(p => p.Fiber), Known(p => p.SaturatedFat));
        var f = weight > 0 ? 100m / weight : 0;
        var per100 = new Per100g(
            Math.Round(totals.Kcal * f, 0, MidpointRounding.AwayFromZero),
            Round(totals.ProteinG * f), Round(totals.CarbsG * f), Round(totals.FatG * f),
            totals.FiberG is { } fiber ? Round(fiber * f) : null,
            totals.SaturatedFatG is { } sat ? Round(sat * f) : null);
        return new RecipeValues(per100, raw, weight, totals);
    }

    private static Totals Exact(Per100g p, decimal grams) =>
        new(p.Kcal * grams / 100, p.Protein * grams / 100, p.Carbs * grams / 100, p.Fat * grams / 100);

    private static decimal Round(decimal value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);
}
