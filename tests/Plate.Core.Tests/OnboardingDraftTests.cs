using FluentAssertions;
using Plate.Core.Domain;
using Plate.Core.Onboarding;

namespace Plate.Core.Tests;

public class OnboardingDraftTests
{
    private const int Year = 2026;

    private static OnboardingDraft Complete() => new()
    {
        Name = "Ivan", Sex = Sex.Male, BirthYear = 1990, HeightCm = 180, WeightKg = 82,
        ActivityLevel = ActivityLevel.LowActive, Goal = Goal.Maintain,
    };

    [Fact]
    public void Keeps_values_inside_the_limits()
    {
        var (draft, rejected) = new OnboardingDraft().Merge(
            new OnboardingFields(Name: " Ivan ", Sex: Sex.Male, BirthYear: 1990, HeightCm: 180, WeightKg: 82.36m, ActivityLevel: ActivityLevel.Active), Year);

        rejected.Should().BeEmpty();
        draft.Name.Should().Be("Ivan");
        draft.Sex.Should().Be(Sex.Male);
        draft.BirthYear.Should().Be(1990);
        draft.HeightCm.Should().Be(180);
        draft.WeightKg.Should().Be(82.4m);
        draft.ActivityLevel.Should().Be(ActivityLevel.Active);
    }

    [Theory]
    [InlineData(OnboardingField.Height)]
    [InlineData(OnboardingField.BirthYear)]
    [InlineData(OnboardingField.Weight)]
    [InlineData(OnboardingField.Protein)]
    [InlineData(OnboardingField.StartingMaintenance)]
    [InlineData(OnboardingField.Name)]
    public void Drops_a_value_outside_the_limits_and_says_why(OnboardingField field)
    {
        var proposed = field switch
        {
            OnboardingField.Height => new OnboardingFields(HeightCm: 300),
            OnboardingField.BirthYear => new OnboardingFields(BirthYear: 2015),
            OnboardingField.Weight => new OnboardingFields(WeightKg: 20),
            OnboardingField.Protein => new OnboardingFields(ProteinGramsPerKg: 5),
            OnboardingField.StartingMaintenance => new OnboardingFields(StartingMaintenanceKcal: 9000),
            _ => new OnboardingFields(Name: new string('a', 51)),
        };
        var before = Complete();

        var (draft, rejected) = before.Merge(proposed, Year);

        draft.Should().Be(before, "the earlier value stays");
        rejected.Should().ContainSingle().Which.Field.Should().Be(field);
    }

    [Fact]
    public void A_later_answer_replaces_an_earlier_one()
    {
        var (draft, _) = Complete().Merge(new OnboardingFields(HeightCm: 175, ActivityLevel: ActivityLevel.Inactive), Year);

        draft.HeightCm.Should().Be(175);
        draft.ActivityLevel.Should().Be(ActivityLevel.Inactive);
        draft.Sex.Should().Be(Sex.Male, "fields the turn didn't mention stay");
    }

    [Fact]
    public void Undefined_enum_values_are_ignored()
    {
        var (draft, _) = Complete().Merge(new OnboardingFields(Sex: (Sex)9, Goal: (Goal)9, ActivityLevel: (ActivityLevel)9), Year);

        draft.Should().Be(Complete());
    }

    [Fact]
    public void A_rate_is_needed_only_for_a_cut_or_bulk()
    {
        Complete().IsComplete.Should().BeTrue();
        (Complete() with { Goal = Goal.Cut }).Missing().Should().Equal(OnboardingField.Rate);
        (Complete() with { Goal = Goal.Cut, RatePercentPerWeek = 0.5m }).IsComplete.Should().BeTrue();
    }

    [Fact]
    public void Missing_lists_every_required_field_in_order()
    {
        new OnboardingDraft().Missing().Should().Equal(
            OnboardingField.Name, OnboardingField.Sex, OnboardingField.BirthYear, OnboardingField.Height,
            OnboardingField.Weight, OnboardingField.ActivityLevel, OnboardingField.Goal);
    }

    [Fact]
    public void A_bulk_faster_than_half_a_percent_is_refused()
    {
        var (draft, rejected) = Complete().Merge(new OnboardingFields(Goal: Goal.Bulk, RatePercentPerWeek: 0.75m), Year);

        draft.Goal.Should().Be(Goal.Bulk);
        draft.RatePercentPerWeek.Should().BeNull();
        rejected.Should().ContainSingle().Which.Field.Should().Be(OnboardingField.Rate);
    }

    [Fact]
    public void Switching_to_bulk_drops_a_cut_rate_that_is_now_too_fast()
    {
        var cut = Complete() with { Goal = Goal.Cut, RatePercentPerWeek = 0.8m };

        var (draft, rejected) = cut.Merge(new OnboardingFields(Goal: Goal.Bulk), Year);

        draft.RatePercentPerWeek.Should().BeNull();
        draft.Missing().Should().Equal(OnboardingField.Rate);
        rejected.Should().ContainSingle().Which.Field.Should().Be(OnboardingField.Rate);
    }

    [Fact]
    public void A_new_profile_seeds_only_the_name()
    {
        var draft = OnboardingDraft.FromProfile(new UserProfile { DisplayName = "Ivan" }, latestWeighInKg: null);

        draft.Name.Should().Be("Ivan");
        draft.Goal.Should().BeNull("the default Maintain is not an answer");
        draft.Missing().Should().NotContain(OnboardingField.Name);
    }

    [Fact]
    public void An_existing_profile_seeds_what_it_knows()
    {
        var profile = new UserProfile
        {
            DisplayName = "Antoan", Sex = Sex.Male, BirthYear = 1996, HeightCm = 180,
            Goal = Goal.Cut, TargetRatePercentPerWeek = 0.5m, ProteinGramsPerKg = 2.2m,
        };

        var draft = OnboardingDraft.FromProfile(profile, latestWeighInKg: 81.2m);

        draft.Missing().Should().Equal(OnboardingField.ActivityLevel);
        draft.WeightKg.Should().Be(81.2m);
        draft.LatestWeighInKg.Should().Be(81.2m);
        draft.ProteinGramsPerKg.Should().Be(2.2m);
    }

    [Fact]
    public void A_filled_in_profile_on_maintain_keeps_its_goal() =>
        OnboardingDraft.FromProfile(new UserProfile { DisplayName = "A", Sex = Sex.Female, HeightCm = 165 }, null).Goal.Should().Be(Goal.Maintain);

    [Fact]
    public void Applies_to_a_copy_of_the_profile_for_the_preview()
    {
        var profile = new UserProfile { UserId = "u", DisplayName = "Old", BarWeightKg = 15 };

        var preview = (Complete() with { Goal = Goal.Bulk, RatePercentPerWeek = 0.25m }).ApplyTo(profile);

        preview.DisplayName.Should().Be("Ivan");
        preview.Goal.Should().Be(Goal.Bulk);
        preview.TargetRatePercentPerWeek.Should().Be(0.25m);
        preview.ProteinGramsPerKg.Should().BeNull("protein defaults to Auto");
        preview.BarWeightKg.Should().Be(15);
        profile.DisplayName.Should().Be("Old");
        ProfileRules.Validate(preview, Year).Should().BeNull();
    }
}
