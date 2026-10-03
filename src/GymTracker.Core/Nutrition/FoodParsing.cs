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
public record ParsedFoodItem(
    string NameEn,
    string NameOriginal,
    decimal Grams,
    FoodState State,
    string? Preparation,
    string? Assumption,
    Per100g Estimated);

public record FoodParseResult(IReadOnlyList<ParsedFoodItem> Items, LocalText? Error, string? Model)
{
    public static FoodParseResult Failed(LocalText error) => new([], error, null);
}

public interface IFoodParser
{
    Task<FoodParseResult> ParseAsync(string text, CancellationToken ct);
}
