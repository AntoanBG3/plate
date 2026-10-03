using GymTracker.Core.Text;

namespace GymTracker.Core.Training;

public record LoggedSet(decimal WeightKg, int Reps, bool IsWarmup = false);

/// <summary>The rules for one exercise as snapshotted on the session.</summary>
public record ProgressionRules(int RepMin, int RepMax, int WorkingSets, decimal IncrementKg, bool IsBodyweight);

public record SetTarget(decimal WeightKg, int Reps);

public enum ProgressionKind
{
    Progress,
    Hold,
    Deload,
}

public record ProgressionSuggestion(ProgressionKind Kind, IReadOnlyList<SetTarget> Sets, LocalText Reason);

/// <summary>Double progression (SPEC 5.1).</summary>
public static class Progression
{
    /// <param name="recentSessions">This exercise's sets from the most recent previous sessions, newest first. Only the first two are used.</param>
    /// <returns>Null when there is no usable history.</returns>
    public static ProgressionSuggestion? Suggest(ProgressionRules rules, IReadOnlyList<IReadOnlyList<LoggedSet>> recentSessions)
    {
        var sessions = recentSessions
            .Select(TopWeightSets)
            .Where(s => s.Count > 0)
            .Take(2)
            .ToList();

        if (sessions.Count == 0)
        {
            return null;
        }

        var last = sessions[0];
        var lastWeight = last[0].WeightKg;
        var increment = rules.IncrementKg > 0 ? rules.IncrementKg : 2.5m;
        var count = Math.Max(1, rules.WorkingSets);

        // Bodyweight with nothing added: only reps progress, never weight.
        if (rules.IsBodyweight && lastWeight == 0)
        {
            var reps = Enumerable.Range(0, count).Select(i => new SetTarget(0, RepsAt(last, i) + 1)).ToList();
            return new ProgressionSuggestion(ProgressionKind.Hold, reps, "Bodyweight: one more rep per set");
        }

        // Deload: below the rep floor at the same weight in each of the last two sessions.
        if (sessions.Count == 2
            && sessions[1][0].WeightKg == lastWeight
            && sessions.All(s => s.Any(x => x.Reps < rules.RepMin)))
        {
            var deloaded = RoundDown(lastWeight * 0.9m, increment);
            return new ProgressionSuggestion(
                ProgressionKind.Deload,
                Repeat(new SetTarget(deloaded, rules.RepMin), count),
                new LocalText("Under {0} reps twice at {1} kg, deload to {2} kg", rules.RepMin, Kg(lastWeight), Kg(deloaded)));
        }

        // Progress: every planned working set reached the top of the range.
        if (last.Count >= count && last.All(x => x.Reps >= rules.RepMax))
        {
            var next = RoundNearest(lastWeight + increment, increment);
            return new ProgressionSuggestion(
                ProgressionKind.Progress,
                Repeat(new SetTarget(next, rules.RepMin), count),
                new LocalText("All sets hit {0}, +{1} kg", rules.RepMax, Kg(increment)));
        }

        // Hold: same weight, one more rep than last time on each set.
        var held = RoundNearest(lastWeight, increment);
        var targets = Enumerable.Range(0, count)
            .Select(i => new SetTarget(held, Math.Min(rules.RepMax, RepsAt(last, i) + 1)))
            .ToList();
        var reason = last.Count < count
            ? new LocalText("Only {0} of {1} sets last time. Hold {2} kg", last.Count, count, Kg(held))
            : new LocalText("Hold {0} kg, add a rep", Kg(held));
        return new ProgressionSuggestion(ProgressionKind.Hold, targets, reason);
    }

    /// <summary>Working sets at the heaviest weight used, in logged order.</summary>
    private static List<LoggedSet> TopWeightSets(IReadOnlyList<LoggedSet> sets)
    {
        var working = sets.Where(s => !s.IsWarmup).ToList();
        if (working.Count == 0)
        {
            return [];
        }

        var top = working.Max(s => s.WeightKg);
        return working.Where(s => s.WeightKg == top).ToList();
    }

    /// <summary>Reps of the i-th set, or of the last set when fewer were logged.</summary>
    private static int RepsAt(List<LoggedSet> sets, int i) => sets[Math.Min(i, sets.Count - 1)].Reps;

    private static IReadOnlyList<SetTarget> Repeat(SetTarget target, int count) =>
        Enumerable.Repeat(target, count).ToList();

    public static decimal RoundNearest(decimal weight, decimal increment) =>
        Math.Round(weight / increment, MidpointRounding.AwayFromZero) * increment;

    public static decimal RoundDown(decimal weight, decimal increment) =>
        Math.Floor(weight / increment) * increment;

    private static string Kg(decimal value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
