using Plate.Core.Text;

namespace Plate.Core.Nutrition;

public enum FoodState
{
    NotApplicable,
    Raw,
    Cooked,
}

/// <summary>What a counted amount counts: pieces ("2 eggs"), whole packs ("half a tub") or servings ("2 servings").</summary>
public enum CountUnit
{
    Piece,
    Pack,
    Serving,
}

/// <summary>
/// One food read from free text. The model supplies what and how much; its own nutrition numbers
/// are only a fallback when no database food matches (SPEC 6.1).
/// </summary>
/// <param name="Units">
/// A count with no weight ("a banana" 1, "2 eggs" 2, "half a tub" 0.5); the matcher makes it grams from the matched food's
/// weight for <paramref name="CountOf"/> (SPEC 6.1 step 4).
/// </param>
/// <param name="CountOf">What <paramref name="Units"/> counts; null reads as pieces (runs stored before it existed).</param>
/// <param name="GramsStated"><paramref name="Grams"/> is a weight or volume the text gives, which wins over any count.</param>
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
    bool AmountGiven = true,
    CountUnit? CountOf = null,
    bool GramsStated = false);

/// <param name="Usage">What OpenRouter counted and charged for the call, when it says.</param>
public record FoodParseResult(IReadOnlyList<ParsedFoodItem> Items, LocalText? Error, string? Model, ParseUsage? Usage = null)
{
    public static FoodParseResult Failed(LocalText error) => new([], error, null);
}

/// <param name="Cost">US dollars, as OpenRouter reports it; null when it doesn't.</param>
public record ParseUsage(int InputTokens, int OutputTokens, int ReasoningTokens, decimal? Cost);

public interface IFoodParser
{
    Task<FoodParseResult> ParseAsync(string text, CancellationToken ct);
}
