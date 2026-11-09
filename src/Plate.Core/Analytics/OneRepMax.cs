using Plate.Core.Training;

namespace Plate.Core.Analytics;

/// <summary>Estimated one-rep max, Epley formula (SPEC 5.2).</summary>
public static class OneRepMax
{
    public const int MaxReps = 12;

    /// <summary>Null for warmups and for sets outside 1 to 12 reps.</summary>
    public static decimal? Estimate(decimal weightKg, int reps)
    {
        if (reps < 1 || reps > MaxReps || weightKg <= 0)
        {
            return null;
        }

        return reps == 1 ? weightKg : weightKg * (1 + reps / 30m);
    }

    /// <summary>The best estimate among a session's working sets, or null if none qualify.</summary>
    public static decimal? Best(IEnumerable<LoggedSet> sets) =>
        sets.Where(s => !s.IsWarmup).Select(s => Estimate(s.WeightKg, s.Reps)).Max();
}
