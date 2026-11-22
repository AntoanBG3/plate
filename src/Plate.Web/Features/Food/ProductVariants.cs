using System.Text.RegularExpressions;

namespace Plate.Web.Features.Food;

/// <summary>Product details that distinguish otherwise similar catalogue names in either language.</summary>
internal static partial class ProductVariants
{
    /// <summary>Brands people write as the name of the product type: "a can of coca-cola" is a cola.</summary>
    private static readonly Dictionary<string, string> BrandProducts = new()
    {
        ["coca cola"] = "cola", ["coke"] = "cola", ["pepsi"] = "cola", ["кока кола"] = "кола", ["пепси"] = "кола",
    };

    /// <summary>The words to look a food up by: the product type for a brand that names one, otherwise the text itself.</summary>
    internal static string LookupName(string query) =>
        BrandProducts.TryGetValue(Normalise(query), out var product) ? product : query;

    /// <summary>
    /// The user's words appear in a USDA name only in its manufacturer field: "The COCA-COLA company, Minute Maid,
    /// Lemonade" is not a cola, and "COCA-COLA, POWERADE" is a sports drink. USDA gives the maker its own comma field,
    /// either named a company or as the first of two all-capital fields ("ABBOTT, ENSURE").
    /// </summary>
    internal static bool MatchesOnlyItsMaker(string name, string query)
    {
        var fields = name.Split(',').Select(f => f.Trim()).ToList();
        var maker = fields.Select((f, i) => i > 0 && (Company().IsMatch(f) || i + 1 < fields.Count && AllCapitals(f) && AllCapitals(fields[i + 1]))).ToList();
        if (!maker.Contains(true))
        {
            return false;
        }

        var wanted = Words(query);
        wanted.ExceptWith(NonIdentity);
        var makerWords = Words(string.Join(' ', fields.Where((_, i) => maker[i])));
        var productWords = Words(string.Join(' ', fields.Where((_, i) => !maker[i])));
        return wanted.Overlaps(makerWords) && !wanted.Overlaps(productWords);

        static bool AllCapitals(string field) => field.Count(char.IsLetter) >= 2 && field.Where(char.IsLetter).All(char.IsUpper);
    }

    [GeneratedRegex(@"\b(company|inc|corp|corporation|llc)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Company();

    // Compare a branded product on its remaining food words. A matching manufacturer alone cannot identify the item.
    internal static bool Compatible(string name, string? brand, string query)
    {
        if (!NamedBrand(brand, query)) return true;
        var brandWords = Words(brand!);
        var identity = Words(Percentage().Replace(query, " "));
        identity.ExceptWith(brandWords);
        identity.ExceptWith(NonIdentity);
        identity.RemoveWhere(w => Variants.Any(variant => variant.IsMatch(w)) || Plate.Core.Nutrition.FoodStates.Stated(w));
        var food = Words(name);
        return identity.Count == 0 ? brandWords.IsSubsetOf(food) : identity.IsSubsetOf(food);
    }

    private static readonly HashSet<string> NonIdentity =
        ["from", "with", "by", "of", "and", "от", "с", "със", "и", "на", "plain", "натурално"];

    private static bool NamedBrand(string? brand, string query) =>
        !string.IsNullOrWhiteSpace(brand) && Normalise(brand) is { Length: > 0 } key
        && (" " + Normalise(query) + " ").Contains(" " + key + " ", StringComparison.Ordinal);

    // Every explicitly named brand includes a whole query token. The brand GIN index supports %> in this direction.
    internal static string[] BrandTokens(string query) => Words(query).ToArray();

    private static HashSet<string> Words(string text) => Normalise(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    private static string Normalise(string text) => Separator().Replace(text.ToLowerInvariant(), " ").Trim();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex Separator();

    // Use equivalent words in both languages. Each pattern matches an explicit variant, including Bulgarian endings.
    private static readonly Regex[] Variants = new[]
    {
        @"fried|пържен[аои]?", @"grilled|на скара", @"smoked|пушен[аои]?",
        @"fat[- ]free|nonfat|обезмаслен[аои]?", @"reduced[- ]fat|low[- ]?fat|нискомаслен[аои]?",
        @"sugar[- ]free|zero|diet|без захар", @"vanilla|ванили[яа]|ванилов[аои]?",
        @"strawberry|strawberries|ягод[аи]|ягодов[аои]?", @"chocolate|шоколад|шоколадов[аои]?",
    }.Select(pattern => new Regex(@"(?<!\p{L})(?:" + pattern + @")(?!\p{L})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)).ToArray();

    [GeneratedRegex(@"(?<![\d.,])(\d{1,3}(?:[.,]\d{1,3})?)\s*%")]
    private static partial Regex Percentage();

    internal static double Adjustment(string name, string? brand, string query)
    {
        double score = NamedBrand(brand, query) && Compatible(name, brand, query) ? 0.45 : 0;
        foreach (var variant in Variants)
        {
            if (!variant.IsMatch(name)) continue;
            score += variant.IsMatch(query) ? 0.25 : -0.25;
        }
        var wanted = Percentage().Match(query);
        var listed = Percentage().Match(name);
        if (wanted.Success && listed.Success)
        {
            var requestedValue = decimal.Parse(wanted.Groups[1].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
            var candidateValue = decimal.Parse(listed.Groups[1].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
            score += requestedValue == candidateValue ? 0.5 : -0.5;
        }
        return score;
    }
}
