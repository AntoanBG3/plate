using FluentAssertions;
using Plate.Core.Nutrition;

namespace Plate.Core.Tests;

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

public class MeatStatesTests
{
    [Theory]
    [InlineData("chicken breast", "chicken breast")]
    [InlineData("Chicken, broilers or fryers, breast, meat only, cooked, roasted", "chicken breast")]
    [InlineData("пилешко филе", "chicken breast")]
    [InlineData("Пилешки гърди, без кост и кожа, сурови", "chicken breast")]
    [InlineData("пилешко бутче", "chicken thigh")]
    [InlineData("chicken thigh fillet", "chicken thigh")]
    [InlineData("пуешко филе", "turkey breast")]
    [InlineData("Turkey, retail parts, breast, meat only, raw", "turkey breast")]
    [InlineData("Chicken breast, roll, oven-roasted", null)]
    [InlineData("Chicken breast tenders, breaded, uncooked", null)]
    [InlineData("ПУШЕНО ПИЛЕШКО ФИЛЕ слайс", null)]
    [InlineData("САНДВИЧ Пилешко филе с пармезан", null)]
    [InlineData("Филе от сьомга", null)]
    [InlineData("свинско филе", null)]
    public void Knows_the_poultry_cuts_weighed_raw_or_cooked(string name, string? cut) =>
        FoodStates.Meat(name).Should().Be(cut);

    [Theory]
    [InlineData("Пилешко филе", FoodState.Raw)]
    [InlineData("Chicken, breast, boneless, skinless, raw", FoodState.Raw)]
    [InlineData("Chicken, broilers or fryers, breast, meat only, cooked, roasted", FoodState.Cooked)]
    [InlineData("Печено пилешко филе", FoodState.Cooked)]
    [InlineData("Chicken, broiler, rotisserie, BBQ, breast, meat only", FoodState.Cooked)]
    public void A_plain_cut_that_says_no_state_is_raw(string name, FoodState state) =>
        FoodStates.OfMeat(name, null).Should().Be(state);

    [Theory]
    [InlineData("пилешко филе сурово", true)]
    [InlineData("raw chicken breast", true)]
    [InlineData("chicken breast grilled", false)]
    [InlineData("пилешко филе на скара", false)]
    [InlineData("пилешко филе", false)]
    public void Only_a_raw_word_says_when_meat_was_weighed(string typed, bool raw) =>
        FoodStates.StatesRaw(typed).Should().Be(raw);

    [Theory]
    [InlineData("basmati rice, dry", true)]
    [InlineData("pasta, uncooked", true)]
    [InlineData("basmati rice, cooked", false)]
    [InlineData("rice", false)]
    public void The_English_name_counts_only_for_dry(string nameEn, bool dry) =>
        FoodStates.SaysDry(nameEn).Should().Be(dry);
}
