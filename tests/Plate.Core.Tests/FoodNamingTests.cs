using FluentAssertions;
using Plate.Core.Nutrition;

namespace Plate.Core.Tests;

/// <summary>A model-written name stands in for the database name only when it can't mislead (SPEC 6.1).</summary>
public class FoodNamingTests
{
    [Theory]
    [InlineData("Chicken breast, skinless, raw", FoodState.Raw, "Chicken breast, skinless, raw")]
    [InlineData("  Chicken   breast,\nskinless ", FoodState.Raw, "Chicken breast, skinless")]
    [InlineData("Пилешко филе, сурово", FoodState.Raw, "Пилешко филе, сурово")]
    [InlineData("Бял ориз, варен", FoodState.Cooked, "Бял ориз, варен")]
    [InlineData("Banana", FoodState.Raw, "Banana")] // leaving the state out is fine
    [InlineData("Oats", FoodState.NotApplicable, "Oats")]
    public void A_fitting_name_is_kept(string proposed, FoodState state, string expected) =>
        FoodNaming.Accept(proposed, state).Should().Be(expected);

    [Theory]
    [InlineData(null, FoodState.Raw)]
    [InlineData("   ", FoodState.Raw)]
    [InlineData("Grilled chicken breast", FoodState.Raw)]
    [InlineData("Chicken breast, raw", FoodState.Cooked)]
    [InlineData("Пилешко на скара", FoodState.Raw)]
    [InlineData("Boiled oats", FoodState.NotApplicable)] // a state the database doesn't say
    [InlineData("200 g chicken breast", FoodState.Raw)]
    public void A_misleading_name_is_rejected(string? proposed, FoodState state) =>
        FoodNaming.Accept(proposed, state).Should().BeNull();

    [Fact]
    public void A_name_over_sixty_characters_is_rejected() =>
        FoodNaming.Accept(new string('a', FoodNaming.MaxLength + 1), FoodState.NotApplicable).Should().BeNull();

    [Theory]
    [InlineData("Raw", FoodState.Raw, "raw")]
    [InlineData(" boiled ", FoodState.Cooked, "boiled")]
    [InlineData("на скара", FoodState.Cooked, "на скара")]
    [InlineData("dried", FoodState.NotApplicable, "dried")]
    [InlineData("canned in water", FoodState.NotApplicable, "canned in water")]
    public void A_fitting_status_is_kept_in_lower_case(string proposed, FoodState state, string expected) =>
        FoodNaming.AcceptStatus(proposed, state).Should().Be(expected);

    [Theory]
    [InlineData(null, FoodState.Raw)]
    [InlineData("", FoodState.Raw)]
    [InlineData("grilled", FoodState.Raw)]
    [InlineData("raw", FoodState.Cooked)]
    [InlineData("сурово", FoodState.Cooked)]
    [InlineData("boiled", FoodState.NotApplicable)]
    [InlineData("cooked for 20 min", FoodState.Cooked)]
    [InlineData("slowly cooked in a pan", FoodState.Cooked)]
    public void A_misleading_or_long_status_is_rejected(string? proposed, FoodState state) =>
        FoodNaming.AcceptStatus(proposed, state).Should().BeNull();
}
