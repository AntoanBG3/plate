namespace Plate.Core.Nutrition;

/// <summary>One food no database food matched, for the estimator (SPEC 6.1 step 6).</summary>
/// <param name="Typed">What the user wrote for it, without the amount.</param>
/// <param name="NameEn">The parser's English name for it.</param>
/// <param name="Grams">The portion, as context only: the values asked for are per 100 g.</param>
/// <param name="Preparation">How it was prepared, as the parser read it; null when the text says nothing.</param>
/// <param name="Nearby">The closest database foods, all judged not to be this food: a reference at most.</param>
public record EstimateRequest(string Typed, string NameEn, decimal Grams, FoodState State, string? Preparation, IReadOnlyList<NearbyFood> Nearby);

/// <summary>A database food close to an unmatched one, with its values per 100 g.</summary>
public record NearbyFood(string Name, Per100g Per100);

/// <summary>The estimator's answer: values per 100 g by request (null where none came back), and the model that gave them.</summary>
public record FoodEstimates(IReadOnlyList<Per100g?> Values, string Model);

/// <summary>
/// Estimates values per 100 g for foods the matcher found no database food for, with a model of its own (SPEC 6.1
/// step 6). The parser's own guesses are the fallback, so a failed or missing answer changes nothing.
/// </summary>
public interface IFoodEstimator
{
    /// <param name="text">Everything the user typed, as context.</param>
    /// <returns>One answer per request, in order; null when estimating failed, is off or has no key.</returns>
    Task<FoodEstimates?> EstimateAsync(string text, IReadOnlyList<EstimateRequest> items, CancellationToken ct);
}

public static class FoodEstimating
{
    /// <summary>
    /// The estimator's values when they can stand in for the parser's: possible per 100 g (<see cref="FoodMath.Validate"/>)
    /// and with some energy. Otherwise null, and the parser's values stay.
    /// </summary>
    public static Per100g? Accept(Per100g? proposed) =>
        proposed is null || proposed.Kcal <= 0 || FoodMath.Validate(100, proposed) is not null ? null : proposed;
}
