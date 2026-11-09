using FluentAssertions;
using Plate.Core.Training;

namespace Plate.Core.Tests;

public class ProgressionTests
{
    private static readonly ProgressionRules Rules = new(RepMin: 8, RepMax: 12, WorkingSets: 3, IncrementKg: 2.5m, IsBodyweight: false);

    private static IReadOnlyList<LoggedSet> Sets(params (decimal Kg, int Reps)[] sets) =>
        sets.Select(s => new LoggedSet(s.Kg, s.Reps)).ToList();

    private static ProgressionSuggestion? Suggest(ProgressionRules rules, params IReadOnlyList<LoggedSet>[] sessions) =>
        Progression.Suggest(rules, sessions);

    [Fact]
    public void No_history_gives_no_suggestion()
    {
        Suggest(Rules).Should().BeNull();
        Suggest(Rules, Sets()).Should().BeNull("a session with no working sets is no history");
    }

    [Fact]
    public void All_sets_at_rep_max_adds_the_increment_and_resets_to_rep_min()
    {
        var s = Suggest(Rules, Sets((80, 12), (80, 12), (80, 12)))!;

        s.Kind.Should().Be(ProgressionKind.Progress);
        s.Sets.Should().HaveCount(3).And.OnlyContain(t => t.WeightKg == 82.5m && t.Reps == 8);
        s.Reason.ToString().Should().Be("All sets hit 12, +2.5 kg");
    }

    [Fact]
    public void Otherwise_holds_the_weight_and_adds_a_rep_per_set_capped_at_rep_max()
    {
        var s = Suggest(Rules, Sets((80, 12), (80, 10), (80, 9)))!;

        s.Kind.Should().Be(ProgressionKind.Hold);
        s.Sets.Select(t => (t.WeightKg, t.Reps)).Should().Equal((80m, 12), (80m, 11), (80m, 10));
        s.Reason.ToString().Should().Be("Hold 80 kg, add a rep");
    }

    [Fact]
    public void Below_rep_min_at_the_same_weight_twice_deloads_ten_percent_rounded_down()
    {
        var s = Suggest(Rules, Sets((80, 8), (80, 7), (80, 6)), Sets((80, 9), (80, 7), (80, 7)))!;

        s.Kind.Should().Be(ProgressionKind.Deload);
        // 80 * 0.9 = 72 → down to 70 on a 2.5 kg increment
        s.Sets.Should().OnlyContain(t => t.WeightKg == 70m && t.Reps == 8);
        s.Reason.ToString().Should().Be("Under 8 reps twice at 80 kg, deload to 70 kg");
    }

    [Fact]
    public void One_prior_session_cannot_deload()
    {
        var s = Suggest(Rules, Sets((80, 6), (80, 5), (80, 5)))!;

        s.Kind.Should().Be(ProgressionKind.Hold);
        s.Sets.Select(t => t.Reps).Should().Equal(7, 6, 6);
    }

    [Fact]
    public void Missing_reps_at_different_weights_does_not_deload()
    {
        var s = Suggest(Rules, Sets((82.5m, 6), (82.5m, 6), (82.5m, 6)), Sets((80, 7), (80, 7), (80, 7)))!;

        s.Kind.Should().Be(ProgressionKind.Hold);
        s.Sets.Should().OnlyContain(t => t.WeightKg == 82.5m);
    }

    [Fact]
    public void Mixed_weights_use_only_the_heaviest_sets()
    {
        // The lighter back-off set at 12 reps must not count toward progression.
        var s = Suggest(Rules, Sets((80, 12), (80, 12), (80, 12), (70, 12), (60, 15)))!;
        s.Kind.Should().Be(ProgressionKind.Progress);
        s.Sets[0].WeightKg.Should().Be(82.5m);

        var hold = Suggest(Rules, Sets((70, 12), (85, 9), (85, 8), (70, 12)))!;
        hold.Kind.Should().Be(ProgressionKind.Hold);
        hold.Sets.Select(t => (t.WeightKg, t.Reps)).Should().Equal((85m, 10), (85m, 9), (85m, 9));
    }

    [Theory]
    [InlineData(1.25, 20, 21.25, 17.5)] // 20 * 0.9 = 18 → down to 17.5, the multiple of 1.25 below
    [InlineData(5, 100, 105, 90)]
    [InlineData(5, 85, 90, 75)]         // 85 * 0.9 = 76.5 → down to 75
    public void Respects_the_increment(decimal increment, decimal weight, decimal progressed, decimal deloaded)
    {
        var rules = Rules with { IncrementKg = increment };

        Suggest(rules, Sets((weight, 12), (weight, 12), (weight, 12)))!.Sets[0].WeightKg.Should().Be(progressed);
        Suggest(rules, Sets((weight, 6), (weight, 6), (weight, 6)), Sets((weight, 7), (weight, 6), (weight, 6)))!
            .Sets[0].WeightKg.Should().Be(deloaded);
    }

    [Fact]
    public void Hold_rounds_an_odd_weight_to_the_nearest_increment()
    {
        var s = Suggest(Rules, Sets((81, 9), (81, 9), (81, 9)))!;
        s.Sets[0].WeightKg.Should().Be(80m);
    }

    [Fact]
    public void Partial_session_does_not_progress_even_if_logged_sets_hit_rep_max()
    {
        var s = Suggest(Rules, Sets((80, 12), (80, 12)))!;

        s.Kind.Should().Be(ProgressionKind.Hold);
        s.Sets.Select(t => (t.WeightKg, t.Reps)).Should().Equal((80m, 12), (80m, 12), (80m, 12));
        s.Reason.ToString().Should().Be("Only 2 of 3 sets last time. Hold 80 kg");
    }

    [Fact]
    public void Warmups_are_ignored()
    {
        var sets = new List<LoggedSet>
        {
            new(40, 5, IsWarmup: true),
            new(60, 3, IsWarmup: true),
            new(80, 12), new(80, 12), new(80, 12),
        };

        Suggest(Rules, sets)!.Kind.Should().Be(ProgressionKind.Progress);

        var onlyWarmups = new List<LoggedSet> { new(40, 10, IsWarmup: true) };
        Suggest(Rules, onlyWarmups).Should().BeNull();
    }

    [Fact]
    public void A_session_with_only_warmups_is_skipped_when_looking_for_the_last_two()
    {
        var warmupOnly = new List<LoggedSet> { new(40, 10, IsWarmup: true) };
        var s = Suggest(Rules, Sets((80, 7), (80, 7), (80, 7)), warmupOnly, Sets((80, 6), (80, 6), (80, 6)))!;

        s.Kind.Should().Be(ProgressionKind.Deload);
    }

    [Fact]
    public void Bodyweight_without_added_weight_progresses_reps_only()
    {
        var rules = Rules with { IsBodyweight = true };
        var s = Suggest(rules, Sets((0, 12), (0, 12), (0, 10)))!;

        s.Sets.Select(t => (t.WeightKg, t.Reps)).Should().Equal((0m, 13), (0m, 13), (0m, 11));
        s.Reason.ToString().Should().Be("Bodyweight: one more rep per set");
    }

    [Fact]
    public void Bodyweight_never_deloads_without_added_weight()
    {
        var rules = Rules with { IsBodyweight = true };
        var s = Suggest(rules, Sets((0, 5), (0, 5), (0, 4)), Sets((0, 5), (0, 4), (0, 4)))!;

        s.Kind.Should().Be(ProgressionKind.Hold);
        s.Sets.Should().OnlyContain(t => t.WeightKg == 0);
    }

    [Fact]
    public void Bodyweight_with_added_weight_follows_the_normal_rules()
    {
        var rules = Rules with { IsBodyweight = true };
        var s = Suggest(rules, Sets((10, 12), (10, 12), (10, 12)))!;

        s.Kind.Should().Be(ProgressionKind.Progress);
        s.Sets[0].WeightKg.Should().Be(12.5m);
    }

    [Fact]
    public void More_planned_sets_than_last_time_repeat_the_last_sets_target()
    {
        var rules = Rules with { WorkingSets = 4 };
        var s = Suggest(rules, Sets((80, 10), (80, 9), (80, 9)))!;

        s.Sets.Select(t => t.Reps).Should().Equal(11, 10, 10, 10);
    }
}
