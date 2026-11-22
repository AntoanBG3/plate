using System.Text.RegularExpressions;

namespace Plate.Web.Features.Food;

/// <summary>Product details that distinguish otherwise similar catalogue names in either language.</summary>
internal static partial class ProductVariants
{
    // A brand used as the product name. Its manufacturer's portfolio also contains water, lemonade and sports drinks.
    internal static string LookupName(string query) =>
        Normalise(query) == "coca cola" ? "cola" : query;

    // Compare a branded product on its remaining food words. A matching manufacturer alone cannot identify the item.
    internal static bool Compatible(string name, string? brand, string query, string lookup)
    {
        if (lookup != query)
        {
            // In USDA names a manufacturer can occupy its own comma-delimited field.
            var fields = name.Split(',');
            var product = fields.Where((part, i) => i == 0 || !Normalise(part).Contains("coca cola", StringComparison.Ordinal));
            if (!Words(string.Join(' ', product)).Contains("cola")) return false;
        }

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
        double score = NamedBrand(brand, query) && Compatible(name, brand, query, query) ? 0.45 : 0;
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
