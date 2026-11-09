namespace Plate.Core.Nutrition;

/// <summary>What the user changed on the Check foods screen before saving an item (SPEC 11).</summary>
[Flags]
public enum ParseCorrection
{
    None = 0,
    Removed = 1,
    GramsChanged = 2,
    ValuesEdited = 4,
    DescriptionEdited = 8,
    MatchChanged = 16,
}

/// <summary>One row as the confirmation list suggested it.</summary>
public record SuggestedItem(string Description, decimal Grams, Per100g Per100, int? FoodItemId);

/// <summary>The same row as the user submitted it.</summary>
public record SavedItem(bool Included, string Description, decimal Grams, Per100g Per100, int? FoodItemId);

public static class ParseCorrections
{
    private const decimal GramsMinChange = 10m;
    private const decimal GramsMinFraction = 0.10m;

    // The number inputs show at most two decimals, so smaller differences are formatting, not edits.
    private const decimal ValueTolerance = 0.005m;

    /// <summary>
    /// Compares each suggested row with the submitted row at the same index. A row missing from the submission
    /// counts as removed; submitted rows beyond the suggestion are ignored (the form never adds rows).
    /// </summary>
    public static IReadOnlyList<ParseCorrection> PerItem(IReadOnlyList<SuggestedItem> suggested, IReadOnlyList<SavedItem> saved) =>
        suggested.Select((s, n) => n < saved.Count ? Compare(s, saved[n]) : ParseCorrection.Removed).ToList();

    public static ParseCorrection Overall(IEnumerable<ParseCorrection> items) =>
        items.Aggregate(ParseCorrection.None, (all, item) => all | item);

    private static ParseCorrection Compare(SuggestedItem s, SavedItem saved)
    {
        if (!saved.Included)
        {
            return ParseCorrection.Removed;
        }

        var result = ParseCorrection.None;
        var gramsMoved = Math.Abs(saved.Grams - s.Grams);
        if (gramsMoved > GramsMinChange && gramsMoved > s.Grams * GramsMinFraction)
        {
            result |= ParseCorrection.GramsChanged;
        }

        if (Differs(s.Per100.Kcal, saved.Per100.Kcal) || Differs(s.Per100.Protein, saved.Per100.Protein)
            || Differs(s.Per100.Carbs, saved.Per100.Carbs) || Differs(s.Per100.Fat, saved.Per100.Fat))
        {
            result |= ParseCorrection.ValuesEdited;
        }

        // Picking another food renames the row after it, so a new name only counts as an edit on the same food.
        if (s.FoodItemId == saved.FoodItemId && Normalize(s.Description) != Normalize(saved.Description))
        {
            result |= ParseCorrection.DescriptionEdited;
        }

        if (s.FoodItemId != saved.FoodItemId)
        {
            result |= ParseCorrection.MatchChanged;
        }

        return result;
    }

    private static bool Differs(decimal a, decimal b) => Math.Abs(Math.Round(a, 2) - Math.Round(b, 2)) > ValueTolerance;

    private static string Normalize(string text) =>
        string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToLowerInvariant();
}
