using FluentAssertions;
using GymTracker.Core.Text;

namespace GymTracker.Core.Tests;

/// <summary>An entry's name never carries its amount; the grams are shown beside it (SPEC 7 Food, Phase 11).</summary>
public class FoodTextTests
{
    [Theory]
    [InlineData("150 g cooked rice", "cooked rice")]
    [InlineData("200g chicken breast", "chicken breast")]
    [InlineData("1.5 kg potatoes", "potatoes")]
    [InlineData("0,5 kg картофи", "картофи")]
    [InlineData("250 ml milk", "milk")]
    [InlineData("200 г пилешки гърди", "пилешки гърди")]
    [InlineData("150 гр. ориз", "ориз")]
    [InlineData("100 grams of oats", "oats")]
    [InlineData("300 мл айрян", "айрян")]
    public void A_leading_amount_is_removed(string text, string expected) =>
        FoodText.StripAmount(text).Should().Be(expected);

    [Theory]
    [InlineData("Banana")]
    [InlineData("7 Days croissant")] // a brand, not an amount
    [InlineData("2% milk")]
    [InlineData("Coca-Cola 330 ml")] // the amount is part of a product's name at the end
    [InlineData("200 g")] // nothing would be left
    public void Anything_else_is_kept(string text) =>
        FoodText.StripAmount(text).Should().Be(text);
}
