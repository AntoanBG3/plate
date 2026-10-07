using FluentAssertions;
using GymTracker.Core.Nutrition;

namespace GymTracker.Core.Tests;

public class LabelValuesTests
{
    private static LabelReading Label(
        LabelBasis basis = LabelBasis.Per100g, decimal? serving = null, decimal? kcal = 250, decimal? kj = null,
        decimal? protein = 10, decimal? carbs = 30, decimal? fat = 10, decimal? sat = 4, decimal? fiber = 2, bool isLabel = true) =>
        new(isLabel, "Кисело мляко", "Danone", basis, serving, kcal, kj, protein, carbs, fat, sat, fiber);

    [Fact]
    public void NotALabelIsNotFound() =>
        LabelValues.Normalize(Label(isLabel: false)).Error.Should().Be(LabelValues.NotFound);

    [Fact]
    public void NothingReadIsNotFound() =>
        LabelValues.Normalize(Label(kcal: null, protein: null, carbs: null, fat: null)).Error.Should().Be(LabelValues.NotFound);

    [Fact]
    public void KilojoulesOnlyBecomeKcal()
    {
        var v = LabelValues.Normalize(Label(kcal: null, kj: 1046)).Values!;
        v.Kcal.Should().Be(250);
        v.Converted.Should().HaveFlag(LabelField.Kcal);
    }

    [Fact]
    public void AgreeingEnergyHasNoNote() =>
        LabelValues.Normalize(Label(kcal: 250, kj: 1046)).Values!.Notes.Should().BeEmpty();

    [Fact]
    public void DisagreeingEnergyKeepsKcalWithNote()
    {
        var v = LabelValues.Normalize(Label(kcal: 250, kj: 1300)).Values!;
        v.Kcal.Should().Be(250);
        v.Notes.Should().Contain(LabelValues.EnergyDisagrees);
    }

    [Fact]
    public void PerServingScalesTo100g()
    {
        var v = LabelValues.Normalize(Label(LabelBasis.PerServing, serving: 30, kcal: 120, protein: 3, carbs: 18, fat: 4, sat: 1.5m, fiber: null)).Values!;
        v.Kcal.Should().Be(400);
        v.Protein.Should().Be(10);
        v.Carbs.Should().Be(60);
        v.Fat.Should().Be(13.3m);
        v.SaturatedFat.Should().Be(5);
        v.Fiber.Should().BeNull();
        v.ServingGrams.Should().Be(30);
        v.Converted.Should().Be(LabelField.Kcal | LabelField.Protein | LabelField.Carbs | LabelField.Fat | LabelField.SaturatedFat);
    }

    [Fact]
    public void PerServingWithoutWeightFails() =>
        LabelValues.Normalize(Label(LabelBasis.PerServing, serving: null)).Error.Should().Be(LabelValues.PerServingOnly);

    [Fact]
    public void Per100mlIsUsedAsPer100gWithNote() =>
        LabelValues.Normalize(Label(LabelBasis.Per100ml)).Values!.Notes.Should().Contain(LabelValues.MillilitresAsGrams);

    [Fact]
    public void RoundsToOneDecimalAndKcalToWhole()
    {
        var v = LabelValues.Normalize(Label(kcal: 250.6m, protein: 12.345m, carbs: 30.05m)).Values!;
        v.Kcal.Should().Be(251);
        v.Protein.Should().Be(12.3m);
        v.Carbs.Should().Be(30.1m);
    }

    [Fact]
    public void ImpossibleValuesAreLeftEmptyWithNote()
    {
        var v = LabelValues.Normalize(Label(kcal: 950, fat: -1)).Values!;
        v.Kcal.Should().BeNull();
        v.Fat.Should().BeNull();
        v.Notes.Should().Contain(LabelValues.LeftEmpty);
    }

    [Fact]
    public void MacrosOver100gWarnButStay()
    {
        var v = LabelValues.Normalize(Label(kcal: 500, protein: 40, carbs: 50, fat: 11)).Values!;
        v.Protein.Should().Be(40);
        v.Warnings.Should().Contain(LabelValues.MacrosOver100);
    }

    [Fact]
    public void SaturatedAboveFatWarns() =>
        LabelValues.Normalize(Label(fat: 3, sat: 5)).Values!.Warnings.Should().Contain(LabelValues.SaturatedAboveFat);

    [Fact]
    public void EnergyThatDoesNotFitMacrosWarns() =>
        LabelValues.Normalize(Label(kcal: 100, protein: 10, carbs: 30, fat: 10)).Values!.Warnings.Should().Contain(LabelValues.EnergyMismatch);

    [Fact]
    public void ConsistentLabelHasNoWarnings() =>
        LabelValues.Normalize(Label(kcal: 250, protein: 10, carbs: 30, fat: 10)).Values!.Warnings.Should().BeEmpty();

    [Theory]
    [InlineData(0)]
    [InlineData(5000)]
    public void ServingOutsideLimitsIsDropped(int grams) =>
        LabelValues.Normalize(Label(serving: grams)).Values!.ServingGrams.Should().BeNull();

    [Fact]
    public void NameAndBrandPassThrough()
    {
        var v = LabelValues.Normalize(Label()).Values!;
        v.Name.Should().Be("Кисело мляко");
        v.Brand.Should().Be("Danone");
    }

    [Theory]
    [InlineData(250, 10, 30, 10, null, false)]
    [InlineData(250, 10.04, 30, 10, null, false)]
    [InlineData(250.4, 10, 30, 10, null, false)]
    [InlineData(250, 10.2, 30, 10, null, true)]
    [InlineData(260, 10, 30, 10, null, true)]
    [InlineData(250, 10, 30, 10, 40.0, true)]
    public void EditedComparesBeyondRounding(double kcal, double protein, double carbs, double fat, double? serving, bool edited)
    {
        var suggested = LabelValues.Normalize(Label(kcal: 250, protein: 10, carbs: 30, fat: 10, sat: 4, fiber: 2)).Values!;
        var saved = new Per100g((decimal)kcal, (decimal)protein, (decimal)carbs, (decimal)fat, 2, 4);
        LabelValues.Edited(suggested, saved, (decimal?)serving).Should().Be(edited);
    }

    [Fact]
    public void FillingAnEmptyValueCountsAsEdited()
    {
        var suggested = LabelValues.Normalize(Label(fiber: null)).Values!;
        LabelValues.Edited(suggested, new Per100g(250, 10, 30, 10, 3, 4), null).Should().BeTrue();
    }
}
