using GymTracker.Core.Text;

namespace GymTracker.Core.Nutrition;

public enum FoodState
{
    NotApplicable,
    Raw,
    Cooked,
}

/// <summary>
/// One food read from free text. The model supplies what and how much; its own nutrition numbers
/// are only a fallback when no database food matches (SPEC 6.1).
/// </summary>
/// <param name="Units">A count with no weight ("a banana", "2 eggs"); the matcher makes it grams from the food's serving.</param>
/// <param name="AmountGiven">
/// The text gives this food's amount as a weight, volume, count or fraction ("200 g", "2 eggs", "half a pack"); false when
/// it gives none ("извара", "a banana"). Only then may the account's last amount replace the grams (SPEC 6.1).
/// </param>
public record ParsedFoodItem(
    string NameEn,
    string NameOriginal,
    decimal Grams,
    FoodState State,
    string? Preparation,
    string? Assumption,
    Per100g Estimated,
    decimal? Units = null,
    bool AmountGiven = true);

public record FoodParseResult(IReadOnlyList<ParsedFoodItem> Items, LocalText? Error, string? Model)
{
    public static FoodParseResult Failed(LocalText error) => new([], error, null);
}

public interface IFoodParser
{
    Task<FoodParseResult> ParseAsync(string text, CancellationToken ct);
}
