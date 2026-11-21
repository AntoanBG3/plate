namespace Plate.Core.Nutrition;

/// <summary>
/// Two database rows that are the same food for scoring: the seed has some foods more than once ("Bananas, raw" from
/// USDA and from a reference table, two "Grapes, green" rows), and picking the other row changes nothing in the log.
/// Same means the same main word and per-100 g values within a few percent; lowfat and full-fat cottage cheese differ.
/// </summary>
public static class FoodEquivalence
{
    public static bool Same(string? nameA, Per100g? a, string? nameB, Per100g? b) =>
        nameA is not null && nameB is not null && a is not null && b is not null
        && Head(nameA) is { Length: > 0 } head && head == Head(nameB)
        && Close(a.Kcal, b.Kcal, 0.05m, 5) && Close(a.Protein, b.Protein, 0.1m, 1)
        && Close(a.Carbs, b.Carbs, 0.1m, 1) && Close(a.Fat, b.Fat, 0.1m, 1);

    /// <summary>The first word, lower case, without a plural ending: "Bananas, raw" and "Banana" both give "banana".</summary>
    private static string Head(string name)
    {
        var word = new string(name.Trim().ToLowerInvariant().TakeWhile(char.IsLetter).ToArray());
        return word.Length <= 3 || word.EndsWith("ss") ? word
            : word.EndsWith("ies") ? word[..^3] + "y"
            : word.EndsWith("oes") ? word[..^2]
            : word.EndsWith('s') ? word[..^1]
            : word;
    }

    private static bool Close(decimal x, decimal y, decimal fraction, decimal floor) =>
        Math.Abs(x - y) <= Math.Max(floor, fraction * Math.Max(Math.Abs(x), Math.Abs(y)));
}
