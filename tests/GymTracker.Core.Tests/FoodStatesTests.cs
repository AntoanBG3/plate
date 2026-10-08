using FluentAssertions;
using GymTracker.Core.Nutrition;

namespace GymTracker.Core.Tests;

/// <summary>The raw or cooked chip beside a food's name comes from the name itself (SPEC 6.1).</summary>
public class FoodStatesTests
{
    [Theory]
    [InlineData("Chicken, broiler or fryers, breast, skinless, boneless, meat only, raw", FoodState.Raw)]
    [InlineData("Chicken, broiler or fryers, breast, skinless, boneless, meat only, cooked, braised", FoodState.Cooked)]
    [InlineData("Bananas, raw", FoodState.Raw)]
    [InlineData("Rice, white, long-grain, regular, enriched, cooked", FoodState.Cooked)]
    [InlineData("Egg, whole, cooked, hard-boiled", FoodState.Cooked)]
    [InlineData("Beef, tripe uncooked, raw", FoodState.Raw)]
    [InlineData("Apples, raw, without skin, cooked, boiled", FoodState.Cooked)]
    [InlineData("Пилешко филе (гърди), без кожа и кост, сурово", FoodState.Raw)]
    [InlineData("Ориз, бял, варен", FoodState.Cooked)]
    [InlineData("Пилешки гърди, на скара", FoodState.Cooked)]
    [InlineData("Картофи, печени", FoodState.Cooked)]
    [InlineData("Яйца, сварени", FoodState.Cooked)]
    public void The_name_says_the_state(string name, FoodState expected) =>
        FoodStates.FromName(name).Should().Be(expected);

    [Theory]
    [InlineData("Strawberries, frozen, unsweetened")]
    [InlineData("Beverages, Kiwi Strawberry Juice Drink")]
    [InlineData("Сушени кайсии")]
    [InlineData("Кисело мляко, 2%")]
    [InlineData("Oats")]
    [InlineData("")]
    [InlineData(null)]
    public void A_name_without_a_state_has_none(string? name) =>
        FoodStates.FromName(name).Should().Be(FoodState.NotApplicable);

    [Fact]
    public void The_bulgarian_name_answers_when_the_english_one_does_not() =>
        FoodStates.FromName("Bulgarian meatballs (kyufte)", "Кюфтета на скара").Should().Be(FoodState.Cooked);

    [Fact]
    public void The_english_name_wins_when_both_say_a_state() =>
        FoodStates.FromName("Spinach, raw", "Спанак, варен").Should().Be(FoodState.Raw);
}
