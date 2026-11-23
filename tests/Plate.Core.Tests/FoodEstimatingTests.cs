using FluentAssertions;
using Plate.Core.Nutrition;

namespace Plate.Core.Tests;

/// <summary>The estimator's values replace the parser's only when they are possible (SPEC 6.1 step 6).</summary>
public class FoodEstimatingTests
{
    [Fact]
    public void Possible_values_are_accepted() =>
        FoodEstimating.Accept(new Per100g(250, 12, 22, 12.5m, 1.5m, 4)).Should().Be(new Per100g(250, 12, 22, 12.5m, 1.5m, 4));

    public static TheoryData<Per100g?> Impossible => new()
    {
        null,
        new Per100g(0, 0, 0, 0),
        new Per100g(950, 10, 10, 10),
        new Per100g(400, 60, 40, 10),
        new Per100g(300, 10, 30, -1),
        new Per100g(300, 10, 30, 10, null, 12),
    };

    [Theory]
    [MemberData(nameof(Impossible))]
    public void Impossible_values_are_refused(Per100g? values) =>
        FoodEstimating.Accept(values).Should().BeNull();
}
