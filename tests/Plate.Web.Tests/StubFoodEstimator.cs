using Plate.Core.Nutrition;

namespace Plate.Web.Tests;

/// <summary>A scripted estimator: answers with whatever <see cref="Answer"/> returns (no answer by default) and records the calls.</summary>
public sealed class StubFoodEstimator : IFoodEstimator
{
    public Func<IReadOnlyList<EstimateRequest>, FoodEstimates?> Answer { get; set; } = _ => null;

    public List<IReadOnlyList<EstimateRequest>> Calls { get; } = [];

    public Task<FoodEstimates?> EstimateAsync(string text, IReadOnlyList<EstimateRequest> items, CancellationToken ct)
    {
        Calls.Add(items);
        return Task.FromResult(Answer(items));
    }
}
