using FluentAssertions;
using Plate.Core.Nutrition;

namespace Plate.Core.Tests;

/// <summary>SPEC 5.12: a recipe's values per 100 g from its ingredients and cooked weight.</summary>
public class RecipeMathTests
{
    // 200 g dry rice (365 kcal, 7.1 P, 80 C, 0.7 F, fiber 1.3, sat 0.2) and 300 g chicken breast (120, 22.5, 0, 2.6, fiber 0, sat 0.6).
    private static readonly RecipePart Rice = new(200, new Per100g(365, 7.1m, 80, 0.7m, 1.3m, 0.2m));
    private static readonly RecipePart Chicken = new(300, new Per100g(120, 22.5m, 0, 2.6m, 0, 0.6m));

    [Fact]
    public void Without_a_cooked_weight_the_raw_weight_is_used()
    {
        var v = RecipeMath.Calculate([Rice, Chicken], null);
        v.WeightGrams.Should().Be(500);
        v.Totals.Kcal.Should().Be(1090);
        v.Per100.Kcal.Should().Be(218);
        v.Per100.Protein.Should().Be(16.3m); // (14.2 + 67.5) / 5
    }

    [Fact]
    public void A_lighter_cooked_dish_concentrates_its_values()
    {
        var v = RecipeMath.Calculate([Rice, Chicken], 350); // 70%
        v.Per100.Kcal.Should().Be(311); // 1090 / 3.5
        v.WeightGrams.Should().Be(350);
    }

    [Fact]
    public void A_heavier_cooked_dish_dilutes_them()
    {
        var v = RecipeMath.Calculate([Rice, Chicken], 1250); // 250%: rice takes up water
        v.Per100.Kcal.Should().Be(87); // 1090 / 12.5 = 87.2
    }

    // A vegetable soup of 500 g raw at 30 kcal per 100 g, so even a fifth of the weight stays possible.
    private static readonly RecipePart Vegetables = new(500, new Per100g(30, 1.5m, 5, 0.3m, 2, 0.1m));

    [Theory]
    [InlineData(99, false)]   // 19.8%
    [InlineData(100, true)]   // 20%
    [InlineData(1500, true)]  // 300%
    [InlineData(1505, false)] // 301%
    public void The_cooked_weight_must_be_20_to_300_percent_of_the_raw_weight(decimal cooked, bool ok) =>
        (RecipeMath.Validate([Vegetables], cooked) is null).Should().Be(ok);

    [Fact]
    public void Fiber_is_known_when_ingredients_with_it_carry_95_percent_of_the_energy()
    {
        // 1000 kcal of known fiber and 52 kcal without: 95.05% covered.
        var known = RecipeMath.Calculate([new(1000, new Per100g(100, 5, 10, 2, 3, 1)), new(52, new Per100g(100, 0, 25, 0))], null);
        known.Per100.Fiber.Should().NotBeNull();
        // 1000 and 53: 94.97%.
        var unknown = RecipeMath.Calculate([new(1000, new Per100g(100, 5, 10, 2, 3, 1)), new(53, new Per100g(100, 0, 25, 0))], null);
        unknown.Per100.Fiber.Should().BeNull();
        unknown.Per100.SaturatedFat.Should().BeNull();
    }

    [Fact]
    public void Known_fiber_adds_up_only_the_ingredients_that_have_it()
    {
        var v = RecipeMath.Calculate([Rice, Chicken, new(5, new Per100g(0, 0, 0, 0))], null); // a pinch of salt, nothing known, 0 kcal
        v.Per100.Fiber.Should().Be(0.5m); // 2.6 g in 505 g
    }

    [Fact]
    public void Zero_ingredients_are_refused() =>
        RecipeMath.Validate([], null).Should().NotBeNull();

    [Fact]
    public void Thirty_one_ingredients_are_refused()
    {
        RecipeMath.Validate(Enumerable.Repeat(Chicken, 30).ToList(), null).Should().BeNull();
        RecipeMath.Validate(Enumerable.Repeat(Chicken, 31).ToList(), null).Should().NotBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3001)]
    public void An_ingredient_must_weigh_1_to_3000_g(decimal grams) =>
        RecipeMath.Validate([new(grams, Chicken.Per100)], null).Should().NotBeNull();

    [Fact]
    public void A_cooked_weight_that_makes_impossible_values_is_refused()
    {
        // Oil reduced to a fifth would be over 4000 kcal per 100 g.
        RecipeMath.Validate([new(100, new Per100g(884, 0, 0, 100))], 20).Should().NotBeNull();
    }
}
