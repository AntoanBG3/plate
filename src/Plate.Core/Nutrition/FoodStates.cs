namespace Plate.Core.Nutrition;

/// <summary>
/// Whether a food's name says it is raw or cooked, for the chip beside an entry's name and the matcher's ranking
/// (SPEC 6.1). Whole words only, so "strawberry" is not raw and "uncooked" is.
/// </summary>
public static class FoodStates
{
    private static readonly HashSet<string> RawWords = ["raw", "uncooked"];

    private static readonly HashSet<string> CookedWords =
        ["cooked", "boiled", "roasted", "grilled", "baked", "fried", "broiled", "steamed", "stewed", "braised",
         "poached", "scrambled", "microwaved", "sauteed", "rotisserie", "barbecued", "bbq"];

    // Bulgarian adjectives change their ending with gender and number ("варен", "варено", "варени").
    private static readonly string[] RawStemsBg = ["суров"];

    private static readonly string[] CookedStemsBg = ["варен", "сварен", "печен", "изпечен", "запечен", "пържен", "задушен", "сготвен", "готвен"];

    private static readonly string[] CookedPhrasesBg = ["на скара", "на грил", "на фурна", "на пара"];

    // Dry staples that take up water when cooked, so 150 g is about 195 kcal cooked or 548 kcal dry for white rice.
    private static readonly string[] StaplesEn = ["rice", "pasta", "spaghetti", "macaroni", "penne", "noodle", "oat", "oatmeal", "bulgur", "quinoa", "couscous", "buckwheat", "lentil", "bean", "chickpea", "barley", "millet"];

    private static readonly string[] StaplesBg = ["ориз", "паста", "спагет", "макарон", "нудл", "овес", "булгур", "киноа", "кус", "елда", "леща", "боб", "нахут", "ечемик", "просо"];

    // Foods made from a staple that are eaten as they are: no cooked and dry versions to choose between.
    private static readonly string[] MadeFrom = ["cake", "cakes", "cracker", "crackers", "flour", "milk", "drink", "chips", "puffed", "flakes", "bran", "cereal", "pudding", "soup", "salad", "bar", "bars",
        "оризовки", "брашно", "мляко", "напитка", "крекери", "вафли", "чипс", "пудинг", "супа", "чорба", "салата", "яхния", "барче", "мюсли", "попара"];

    // Words in what the user typed that say the weight is dry, beside the raw ones.
    private static readonly string[] DryWords = ["dry", "uncooked", "сух", "суха", "сухи", "сухо", "неварен", "неварена", "неварени"];

    // Poultry cuts people weigh either before or after cooking, which loses about a quarter of the weight as water:
    // 200 g of chicken breast is about 240 kcal weighed raw and 330 kcal weighed cooked (QA 2026-11-24 M7).
    // Each is (the cut, words for the animal, words for the part); Bulgarian ones are word beginnings.
    private static readonly (string Cut, string[] Animal, string[] Part)[] MeatsEn =
    [
        // Thigh first, so a thigh fillet is a thigh.
        ("chicken thigh", ["chicken"], ["thigh", "thighs"]),
        ("chicken breast", ["chicken"], ["breast", "breasts", "fillet", "fillets"]),
        ("turkey breast", ["turkey"], ["breast", "breasts", "fillet", "fillets"]),
    ];

    private static readonly (string Cut, string[] Animal, string[] Part)[] MeatsBg =
    [
        ("chicken thigh", ["пилешк", "пиле"], ["бут"]),
        ("chicken breast", ["пилешк", "пиле"], ["филе", "гърд"]),
        ("turkey breast", ["пуешк", "пуйка"], ["филе", "гърд"]),
    ];

    // Products and dishes made from the cut, which are eaten as they are: deli meat, breaded pieces, sandwiches.
    private static readonly string[] NotACutEn = ["breaded", "breading", "batter", "flour", "nugget", "nuggets", "patty", "patties", "sandwich", "sub", "salad",
        "soup", "roll", "deli", "sliced", "luncheon", "smoked", "prepackaged", "sausage", "ham", "frankfurter", "fast", "foods", "babyfood", "pie", "pizza"];

    private static readonly string[] NotACutBg = ["панир", "сандвич", "салата", "супа", "роле", "нарязан", "слайс", "пушен", "шунка", "деликатес", "кренвирш",
        "наденица", "хапки", "бургер", "трамезини", "пица", "дюнер", "баница", "пай", "крокет", "кюфте"];

    /// <summary>The English word that names the staple in <paramref name="name"/> ("rice" for "basmati rice"), or null when it isn't one.</summary>
    public static string? Staple(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var words = name.ToLowerInvariant().Split([' ', ',', '(', ')', '-', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(MadeFrom.Contains))
        {
            return null;
        }

        foreach (var w in words)
        {
            var en = StaplesEn.FirstOrDefault(s => w == s || w == s + "s" || w == s + "es");
            if (en is not null)
            {
                return en is "spaghetti" or "macaroni" or "penne" or "noodle" ? "pasta" : en is "oatmeal" ? "oat" : en;
            }

            var bg = Array.FindIndex(StaplesBg, s => w.StartsWith(s, StringComparison.Ordinal));
            if (bg >= 0)
            {
                return bg switch { 0 => "rice", 1 or 2 or 3 or 4 => "pasta", 5 => "oat", 6 => "bulgur", 7 => "quinoa", 8 => "couscous", 9 => "buckwheat", 10 => "lentil", 11 => "bean", 12 => "chickpea", 13 => "barley", _ => "millet" };
            }
        }

        return null;
    }

    /// <summary>
    /// A staple food's state from its name, or else from its energy: dry grains and pasta have 300 to 380 kcal per 100 g
    /// and cooked ones 100 to 200, so a product named "Ориз Басмати" at 357 kcal is dry.
    /// A name and an energy that disagree ("Lentils, raw" at 105 kcal, a reference row with cooked values) give no state.
    /// </summary>
    public static FoodState OfStaple(string? name, string? nameBg, decimal kcalPer100g)
    {
        var byEnergy = kcalPer100g >= 250 ? FoodState.Raw : kcalPer100g is > 0 and <= 200 ? FoodState.Cooked : FoodState.NotApplicable;
        var byName = FromName(name, nameBg);
        return byName == FoodState.NotApplicable ? byEnergy
            : byEnergy == FoodState.NotApplicable || byEnergy == byName ? byName
            : FoodState.NotApplicable;
    }

    /// <summary>
    /// The poultry cut <paramref name="name"/> is ("chicken breast" for "пилешко филе" or "Chicken, broilers or fryers,
    /// breast, meat only, cooked, roasted"), or null when it isn't one or is a product made from it (deli slices, breaded
    /// pieces, a sandwich).
    /// </summary>
    public static string? Meat(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var words = name.ToLowerInvariant().Split([' ', ',', '(', ')', '-', '/', '.', ';', ':'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(NotACutEn.Contains) || words.Any(w => NotACutBg.Any(w.StartsWith)))
        {
            return null;
        }

        foreach (var (cut, animal, part) in MeatsEn)
        {
            if (words.Any(animal.Contains) && words.Any(part.Contains))
            {
                return cut;
            }
        }

        foreach (var (cut, animal, part) in MeatsBg)
        {
            if (words.Any(w => animal.Any(w.StartsWith)) && words.Any(w => part.Any(w.StartsWith)))
            {
                return cut;
            }
        }

        return null;
    }

    /// <summary>A poultry cut's state from its names; a plain cut that says neither ("Пилешко филе") is sold raw.</summary>
    public static FoodState OfMeat(string? name, string? nameBg) =>
        FromName(name, nameBg) is var state && state != FoodState.NotApplicable ? state : FoodState.Raw;

    /// <summary>
    /// What the user typed says the meat was weighed raw ("суров", "raw"). A cooking word ("на скара", "grilled") says
    /// how it was made, not when it was weighed, so it doesn't count.
    /// </summary>
    public static bool StatesRaw(string? typed) => Of(typed) == FoodState.Raw;

    /// <summary>
    /// The parser's English name says the weight is dry ("basmati rice, dry" for "ориз басмати 80г сух"), which it
    /// sometimes keeps when it drops the word from the original name (QA 2026-11-24 L1). Only dry words count: the parser
    /// adds its own cooked reading of rice to the English name, so a cooking word there says nothing about the user.
    /// </summary>
    public static bool SaysDry(string? nameEn) =>
        (nameEn ?? "").ToLowerInvariant().Split([' ', ',', '(', ')', '-', '/', '.'], StringSplitOptions.RemoveEmptyEntries).Any(w => w is "dry" or "uncooked");

    /// <summary>What the user typed says whether the weight is raw (or dry) or cooked: "150 г сварен ориз", "80 g dry pasta".</summary>
    public static bool Stated(string? typed) =>
        FromName(typed) != FoodState.NotApplicable
        || (typed ?? "").ToLowerInvariant().Split([' ', ',', '(', ')', '-', '/', '.'], StringSplitOptions.RemoveEmptyEntries).Any(DryWords.Contains);

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
