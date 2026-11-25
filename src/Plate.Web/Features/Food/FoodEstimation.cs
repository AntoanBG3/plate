using Plate.Core.Nutrition;

namespace Plate.Web.Features.Food;

/// <summary>
/// Asks the estimator for the rows no database food matched (SPEC 6.1 step 6) and puts its values on them. Rows it
/// doesn't answer for, or answers with impossible values, keep the parser's own estimate.
/// </summary>
public static class FoodEstimation
{
    /// <param name="nearby">The matcher's closest database foods per item (<see cref="FoodMatcher.ResolveAsync"/>).</param>
    public static async Task<List<ConfirmItem>> ApplyAsync(IFoodEstimator? estimator, string text, IReadOnlyList<ParsedFoodItem> parsed,
        IReadOnlyList<ConfirmItem> rows, IReadOnlyList<IReadOnlyList<NearbyFood>> nearby, CancellationToken ct)
    {
        var result = rows.ToList();
        var open = rows.Select((row, n) => (row, n)).Where(x => Open(x.row, x.n, parsed)).ToList();
        if (estimator is null || open.Count == 0)
        {
            return result;
        }

        var requests = open.Select(x => new EstimateRequest(x.row.Typed, parsed[x.n].NameEn, x.row.Grams, x.row.State, parsed[x.n].Preparation,
            nearby.ElementAtOrDefault(x.n) ?? [])).ToList();
        var answer = await estimator.EstimateAsync(text, requests, ct);
        for (var i = 0; i < open.Count && answer is not null; i++)
        {
            if (FoodEstimating.Accept(answer.Values.ElementAtOrDefault(i)) is { } values)
            {
                result[open[i].n] = FoodMatcher.WithEstimate(open[i].row, values, answer.Model);
            }
        }

        return result;
    }

    /// <summary>Some row has no database food, so <see cref="ApplyAsync"/> would ask the estimator.</summary>
    public static bool Needed(IReadOnlyList<ConfirmItem> rows, IReadOnlyList<ParsedFoodItem> parsed) =>
        rows.Where((row, n) => Open(row, n, parsed)).Any();

    private static bool Open(ConfirmItem row, int n, IReadOnlyList<ParsedFoodItem> parsed) =>
        row.FoodItemId is null && row.Confidence == Confidence.Low && n < parsed.Count;
}
