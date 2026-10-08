namespace GymTracker.Core.Nutrition;

/// <summary>
/// Whether a food's name says it is raw or cooked, for the chip beside an entry's name and the matcher's ranking
/// (SPEC 6.1). Whole words only, so "strawberry" is not raw and "uncooked" is.
/// </summary>
public static class FoodStates
{
    private static readonly HashSet<string> RawWords = ["raw", "uncooked"];

    private static readonly HashSet<string> CookedWords =
        ["cooked", "boiled", "roasted", "grilled", "baked", "fried", "broiled", "steamed", "stewed", "braised",
         "poached", "scrambled", "microwaved", "sauteed"];

    // Bulgarian adjectives change their ending with gender and number ("варен", "варено", "варени").
    private static readonly string[] RawStemsBg = ["суров"];

    private static readonly string[] CookedStemsBg = ["варен", "сварен", "печен", "изпечен", "запечен", "пържен", "задушен", "сготвен", "готвен"];

    private static readonly string[] CookedPhrasesBg = ["на скара", "на грил", "на фурна", "на пара"];

    /// <summary>The first name that says a state wins; a name that says both ("Apples, raw, without skin, cooked, boiled") is cooked.</summary>
    public static FoodState FromName(string? name, string? nameBg = null)
    {
        var state = Of(name);
        return state == FoodState.NotApplicable ? Of(nameBg) : state;
    }

    private static FoodState Of(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return FoodState.NotApplicable;
        }

        var lower = name.ToLowerInvariant();
        var words = lower.Split([' ', ',', '(', ')', '-', '/', '.', ';', ':'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(CookedWords.Contains) || words.Any(w => CookedStemsBg.Any(w.StartsWith))
            || CookedPhrasesBg.Any(p => (" " + string.Join(' ', words) + " ").Contains(" " + p + " ")))
        {
            return FoodState.Cooked;
        }

        return words.Any(RawWords.Contains) || words.Any(w => RawStemsBg.Any(w.StartsWith)) ? FoodState.Raw : FoodState.NotApplicable;
    }
}
