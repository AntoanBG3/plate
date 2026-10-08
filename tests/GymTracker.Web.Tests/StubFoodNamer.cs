using GymTracker.Core.Nutrition;

namespace GymTracker.Web.Tests;

/// <summary>A scripted namer: answers with whatever <see cref="Answer"/> returns (no names by default) and records the calls.</summary>
public sealed class StubFoodNamer : IFoodNamer
{
    public Func<IReadOnlyList<NameRequest>, IReadOnlyList<FoodName?>?> Answer { get; set; } = _ => null;

    public List<IReadOnlyList<NameRequest>> Calls { get; } = [];

    public Task<IReadOnlyList<FoodName?>?> NameAsync(IReadOnlyList<NameRequest> items, CancellationToken ct)
    {
        Calls.Add(items);
        return Task.FromResult(Answer(items));
    }
}
