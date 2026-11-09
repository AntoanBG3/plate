using FluentAssertions;
using Plate.Core.Nutrition;
using Plate.Core.Text;

namespace Plate.Core.Tests;

public class ParseCorrectionsTests
{
    private static readonly Per100g Chicken = new(120, 22.5m, 0, 2.6m);
    private static readonly SuggestedItem Suggested = new("chicken breast", 200, Chicken, 7);

    private static SavedItem Same(bool included = true) => new(included, "chicken breast", 200, Chicken, 7);

    private static ParseCorrection One(SavedItem saved) => ParseCorrections.PerItem([Suggested], [saved]).Single();

    [Fact]
    public void Saving_what_was_suggested_is_no_correction() =>
        One(Same()).Should().Be(ParseCorrection.None);

    [Fact]
    public void An_unticked_item_is_removed_and_nothing_else_counts() =>
        One(Same(included: false) with { Grams = 50, Description = "rice" }).Should().Be(ParseCorrection.Removed);

    [Theory]
    [InlineData(215, false)]   // 15 g but only 7.5%
    [InlineData(221, true)]    // 21 g and 10.5%
    [InlineData(185, false)]
    [InlineData(150, true)]
    public void Grams_count_when_they_move_more_than_10_percent_and_10_g(decimal grams, bool changed) =>
        One(Same() with { Grams = grams }).HasFlag(ParseCorrection.GramsChanged).Should().Be(changed);

    [Fact]
    public void Small_portions_need_a_10_g_change_too() =>
        ParseCorrections.PerItem([Suggested with { Grams = 20 }], [Same() with { Grams = 28 }]).Single().Should().Be(ParseCorrection.None);

    [Fact]
    public void A_zero_gram_suggestion_counts_any_change_over_10_g() =>
        ParseCorrections.PerItem([Suggested with { Grams = 0 }], [Same() with { Grams = 30 }]).Single().Should().Be(ParseCorrection.GramsChanged);

    [Fact]
    public void Rounding_from_the_number_inputs_is_not_an_edit() =>
        One(Same() with { Per100 = Chicken with { Protein = 22.501m, Fat = 2.6049m } }).Should().Be(ParseCorrection.None);

    [Fact]
    public void Editing_any_per_100_g_value_counts() =>
        One(Same() with { Per100 = Chicken with { Carbs = 3 } }).Should().Be(ParseCorrection.ValuesEdited);

    [Fact]
    public void Description_edits_ignore_case_and_spacing()
    {
        One(Same() with { Description = "  Chicken  Breast " }).Should().Be(ParseCorrection.None);
        One(Same() with { Description = "chicken thigh" }).Should().Be(ParseCorrection.DescriptionEdited);
    }

    [Fact]
    public void A_different_food_is_a_match_change() =>
        One(Same() with { FoodItemId = 8 }).Should().Be(ParseCorrection.MatchChanged);

    [Fact]
    public void A_new_name_that_came_with_a_different_food_is_not_a_description_edit() =>
        One(Same() with { Description = "Chicken thigh, raw", FoodItemId = 8 }).Should().Be(ParseCorrection.MatchChanged);

    [Fact]
    public void Several_edits_combine()
    {
        One(Same() with { Grams = 300, Description = "thigh", Per100 = Chicken with { Carbs = 3 } }).Should()
            .Be(ParseCorrection.GramsChanged | ParseCorrection.DescriptionEdited | ParseCorrection.ValuesEdited);
        One(Same() with { Grams = 300, FoodItemId = null }).Should().Be(ParseCorrection.GramsChanged | ParseCorrection.MatchChanged);
    }

    [Fact]
    public void Items_missing_from_a_tampered_form_count_as_removed_and_extras_are_ignored()
    {
        ParseCorrections.PerItem([Suggested, Suggested], [Same()]).Should().Equal(ParseCorrection.None, ParseCorrection.Removed);
        ParseCorrections.PerItem([Suggested], [Same(), Same() with { Grams = 1 }]).Should().Equal(ParseCorrection.None);
    }

    [Fact]
    public void Overall_combines_every_item() =>
        ParseCorrections.Overall([ParseCorrection.Removed, ParseCorrection.None, ParseCorrection.GramsChanged])
            .Should().Be(ParseCorrection.Removed | ParseCorrection.GramsChanged);
}

public class ErrorFingerprintTests
{
    [Fact]
    public void Ids_and_numbers_in_messages_and_paths_do_not_split_one_bug()
    {
        var a = ErrorFingerprint.Of("NullReferenceException", "Entry 123 not found for 9f1c2a3b-1d2e-4f5a-8b9c-0d1e2f3a4b5c", "at Plate.Web.Features.Food.FoodLogService.GetAsync()", "/food/entries/123");
        var b = ErrorFingerprint.Of("NullReferenceException", "Entry 77 not found for 00000000-1111-2222-3333-444444444444", "at Plate.Web.Features.Food.FoodLogService.GetAsync()", "/food/entries/77");

        a.Should().Be(b).And.HaveLength(16);
    }

    [Fact]
    public void Quoted_values_are_ignored() =>
        ErrorFingerprint.Of("FormatException", "Value 'abc' is not valid", null, "/x").Should()
            .Be(ErrorFingerprint.Of("FormatException", "Value 'xyz' is not valid", null, "/x"));

    [Fact]
    public void Different_types_frames_or_pages_are_different_bugs()
    {
        var baseline = ErrorFingerprint.Of("InvalidOperationException", "boom", "at Plate.A.B()", "/food");

        ErrorFingerprint.Of("ArgumentException", "boom", "at Plate.A.B()", "/food").Should().NotBe(baseline);
        ErrorFingerprint.Of("InvalidOperationException", "boom", "at Plate.A.C()", "/food").Should().NotBe(baseline);
        ErrorFingerprint.Of("InvalidOperationException", "boom", "at Plate.A.B()", "/train").Should().NotBe(baseline);
    }

    [Theory]
    [InlineData("/food/entries/123", "/food/entries/{id}")]
    [InlineData("/train/sessions/5/summary", "/train/sessions/{id}/summary")]
    [InlineData("/settings/passkeys/AbC_-x/remove", "/settings/passkeys/AbC_-x/remove")]
    [InlineData("/food?date=2026-10-04", "/food")]
    [InlineData(null, "")]
    public void Path_templates_replace_numeric_segments_and_drop_the_query(string? path, string template) =>
        ErrorFingerprint.PathTemplate(path).Should().Be(template);

    [Fact]
    public void Only_the_first_in_app_frame_counts()
    {
        const string stack = "   at System.Linq.Enumerable.First()\n   at Plate.Web.Features.Food.FoodLogService.GetAsync(Int32 id) in /src/x.cs:line 12\n   at Plate.Web.Controllers.FoodController.Entry()";

        ErrorFingerprint.FirstAppFrame(stack).Should().Be("Plate.Web.Features.Food.FoodLogService.GetAsync(Int32 id)");
        ErrorFingerprint.FirstAppFrame("at render (https://plate.peychev.uk/js/nav.js?v=abc:10:5)").Should().Be("render (/js/nav.js)");
    }
}

public class ParseAgreementTests
{
    private static readonly Per100g Values = new(100, 10, 10, 1);

    private static SavedItem Saved(int? food, decimal grams, bool included = true, string name = "x") => new(included, name, grams, Values, food);

    private static SuggestedItem Fresh(int? food, decimal grams, string name = "x") => new(name, grams, Values, food);

    [Fact]
    public void Items_pair_by_food_whatever_their_order()
    {
        var result = ParseAgreement.Score([Saved(1, 200), Saved(2, 120)], [Fresh(2, 120), Fresh(1, 200)]);

        result.Should().Be(new AgreementResult(2, 2, 2, 0));
    }

    [Fact]
    public void Grams_count_only_within_the_correction_tolerance() =>
        ParseAgreement.Score([Saved(1, 200), Saved(2, 100)], [Fresh(1, 215), Fresh(2, 150)]).Should().Be(new AgreementResult(2, 2, 1, 0));

    [Fact]
    public void Unticked_items_are_not_expected_and_new_items_nobody_saved_are_extra() =>
        ParseAgreement.Score([Saved(1, 200), Saved(3, 50, included: false)], [Fresh(1, 200), Fresh(3, 50), Fresh(4, 10)])
            .Should().Be(new AgreementResult(1, 1, 1, 2));

    [Fact]
    public void AI_estimates_pair_by_name_and_a_different_food_is_a_miss() =>
        ParseAgreement.Score([Saved(null, 100, name: "Баница"), Saved(1, 100)], [Fresh(null, 100, name: "баница"), Fresh(9, 100)])
            .Should().Be(new AgreementResult(2, 1, 1, 1));

    [Fact]
    public void One_fresh_item_answers_only_one_saved_item() =>
        ParseAgreement.Score([Saved(1, 100), Saved(1, 100)], [Fresh(1, 100)]).Should().Be(new AgreementResult(2, 1, 1, 0));

    [Fact]
    public void Results_add_up() =>
        (new AgreementResult(2, 1, 1, 0) + new AgreementResult(3, 3, 2, 1)).Should().Be(new AgreementResult(5, 4, 3, 1));
}

public class ModelAccuracyTests
{
    [Fact]
    public void No_runs_give_no_figures() =>
        ModelAccuracy.Summarize([]).Should().BeEmpty();

    [Fact]
    public void Corrections_count_against_saved_parses_and_ratings_against_all()
    {
        var runs = Enumerable.Range(0, 20).Select(n => new RunOutcome("a", Confirmed: n < 10, Corrected: n < 3, RatedDown: n == 15)).ToList();

        var stats = ModelAccuracy.Summarize(runs)["a"];

        stats.Should().Be(new ModelStats("a", 20, 10, 0.3m, 0.05m, true));
    }

    [Fact]
    public void Runs_never_saved_have_no_correction_rate() =>
        ModelAccuracy.Summarize([new RunOutcome("a", false, false, true)])["a"].Should().Be(new ModelStats("a", 1, 0, null, 1m, false));

    [Fact]
    public void Each_model_is_counted_on_its_own_and_fewer_than_20_runs_is_not_enough()
    {
        var stats = ModelAccuracy.Summarize([.. Enumerable.Repeat(new RunOutcome("a", true, false, false), 19), new RunOutcome("b", true, true, false)]);

        stats["a"].Enough.Should().BeFalse();
        (stats["b"].Runs, stats["b"].CorrectionRate).Should().Be((1, 1m));
    }
}
