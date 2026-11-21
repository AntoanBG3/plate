using FluentAssertions;
using Plate.Core.Nutrition;
using Xunit;

namespace Plate.Core.Tests;

/// <summary>SPEC 6.1 step 4: the grams for a parsed amount once a database food is matched.</summary>
public class PortionsTests
{
    private static ParsedFoodItem Item(decimal grams, decimal? units = null, CountUnit? of = null, bool stated = false, string? assumption = null) =>
        new("x", "x", grams, FoodState.NotApplicable, null, assumption, new Per100g(100, 5, 10, 3), units, AmountGiven: stated || units is not null, CountOf: of, GramsStated: stated);

    private static readonly UnitWeights LidlPastry = new(Piece: 90, Pack: null, Serving: 90);

    [Fact]
    public void Two_pieces_use_the_piece_weight()
    {
        var portion = Portions.For(Item(150, 2, CountUnit.Piece), LidlPastry);

        portion.Should().Be(new Portion(180, PortionBasis.Count, portion.Note));
        portion.Note!.ToString().Should().Be("2 × a piece of 90 g");
        Math.Round(portion.Grams * 233 / 100).Should().Be(419, "the catalogue's 233 kcal per 100 g");
    }

    [Fact]
    public void A_stated_total_weight_wins_over_the_count() =>
        Portions.For(Item(165, 2, CountUnit.Piece, stated: true), LidlPastry).Should().Be(new Portion(165, PortionBasis.Stated, null));

    [Fact]
    public void A_serving_of_several_pieces_is_not_a_piece()
    {
        // A label's "2 biscuits (30 g)": the piece is 15 g.
        Portions.For(Item(40, 2), new UnitWeights(Piece: 15, Pack: null, Serving: 30)).Grams.Should().Be(30);

        var servingOnly = Portions.For(Item(40, 2), new UnitWeights(null, null, Serving: 30));
        servingOnly.Grams.Should().Be(40, "a serving alone gives no piece weight, so the parser's estimate stands");
        servingOnly.Basis.Should().Be(PortionBasis.Parser);
        servingOnly.Note!.Format.Should().Be(Portions.NoPieceNote);
    }

    [Fact]
    public void Half_a_pack_uses_the_pack_weight_and_without_one_says_to_check()
    {
        Portions.For(Item(150, 0.5m, CountUnit.Pack), new UnitWeights(null, Pack: 400, Serving: 125)).Grams.Should().Be(200);

        var unknown = Portions.For(Item(150, 0.5m, CountUnit.Pack), new UnitWeights(null, null, Serving: 125));
        unknown.Should().Be(new Portion(150, PortionBasis.Parser, unknown.Note));
        unknown.Note!.Format.Should().Be(Portions.NoPackNote, "the usual serving is not the pack");
    }

    [Fact]
    public void Servings_use_the_serving() =>
        Portions.For(Item(100, 2, CountUnit.Serving), new UnitWeights(null, null, 30)).Grams.Should().Be(60);

    [Fact]
    public void No_count_keeps_the_parser_grams_and_a_parser_assumption_needs_no_second_note()
    {
        Portions.For(Item(120), LidlPastry).Should().Be(new Portion(120, PortionBasis.Parser, null));
        Portions.For(Item(120, 1, assumption: "assumed one medium banana, 120 g"), new UnitWeights(null, null, null)).Note.Should().BeNull();
    }

    [Fact]
    public void A_count_from_runs_stored_before_the_unit_reads_as_pieces() =>
        Portions.For(Item(100, 3), new UnitWeights(Piece: 50, null, null)).Grams.Should().Be(150);
}

public class WeighedMealsTests
{
    private static WeighedResult Result(string id, string category, decimal weighedKcal, decimal plateKcal, bool uncertain = false) =>
        new(new WeighedMeal(id, null, null, category, "x", [new WeighedItem("food", 100, new Per100g(weighedKcal, 10, 10, 5), "label", uncertain)]),
            new MealNutrients(plateKcal, 10, 10, 5));

    [Fact]
    public void The_reference_is_the_weighed_grams_times_the_values()
    {
        var meal = new WeighedMeal("1", null, null, "counted", "две закуски", [
            new WeighedItem("pastry", 180, new Per100g(233, 7.6m, 23.3m, 11.9m), "estimate", false),
            new WeighedItem("ayran", 250, new Per100g(37, 1.7m, 2.6m, 2), "label", false)]);

        meal.Reference.Should().Be(new MealNutrients(511.9m, 17.9m, 48.4m, 26.4m));
    }

    [Fact]
    public void Uncertain_meals_are_left_out_and_the_largest_misses_come_first()
    {
        var summary = WeighedMeals.Summarize([
            Result("a", "counted", 400, 420), Result("b", "staple", 300, 500), Result("c", "staple", 200, 150), Result("d", "loose", 600, 100, uncertain: true)]);

        summary.Uncertain.Should().Be(1);
        summary.Overall.Meals.Should().Be(3);
        summary.Overall.MeanAbsKcal.Should().Be(90);
        summary.Overall.MeanSignedKcal.Should().Be(56.7m);
        summary.ByCategory.Keys.Should().Equal("counted", "staple");
        summary.ByCategory["staple"].MeanAbsKcal.Should().Be(125);
        summary.LargestMisses.Select(r => r.Meal.Id).Should().Equal("b", "c", "a");
    }
}

public class UnitWeightTextTests
{
    [Theory]
    [InlineData("2 biscuits (30 g)", 30.0, 15.0)]
    [InlineData("3 бисквити = 25 г", 25.0, 8.3)]
    [InlineData("1 piece (25g)", 25.0, 25.0)]
    [InlineData("2 slices (50 g)", 50.0, 25.0)]
    [InlineData("30 g", 30.0, null)]
    [InlineData("1 portion (30 g)", 30.0, null)]
    [InlineData("1 порция (125 г)", 125.0, null)]
    [InlineData("2 tbsp (30 g)", 30.0, null)]
    [InlineData("2 biscuits", 30.0, null)]
    [InlineData(null, 30.0, null)]
    public void A_piece_weight_needs_a_counted_serving(string? text, double serving, double? piece) =>
        UnitWeightText.PieceGrams(text, (decimal)serving).Should().Be(piece is null ? null : (decimal)piece);

    [Theory]
    [InlineData(400.0, "g", null, 400.0)]
    [InlineData(1.0, "kg", null, 1000.0)]
    [InlineData(500.0, "ml", null, null)]
    [InlineData(null, null, "400 g", 400.0)]
    [InlineData(null, null, "400г", 400.0)]
    [InlineData(null, null, "500 ml", null)]
    [InlineData(null, null, "4 x 125 g", null)]
    [InlineData(5000.0, "g", null, null)]
    public void A_pack_weight_is_a_weight(double? quantity, string? unit, string? text, double? grams) =>
        UnitWeightText.PackGrams((decimal?)quantity, unit, text).Should().Be(grams is null ? null : (decimal)grams);
}
