namespace GymTracker.Core.Nutrition;

/// <summary>One parse as the accuracy figures see it.</summary>
public record RunOutcome(string Model, bool Confirmed, bool Corrected, bool RatedDown);

/// <param name="CorrectionRate">Share of saved parses the user changed before saving; null before any were saved.</param>
/// <param name="DownRate">Share of parses rated thumbs down.</param>
/// <param name="Enough">Whether there are enough parses for the rates to mean anything.</param>
public record ModelStats(string Model, int Runs, int Confirmed, decimal? CorrectionRate, decimal DownRate, bool Enough);

/// <summary>How often each model's parses were corrected or rated down (SPEC 11), for the model pages in Settings.</summary>
public static class ModelAccuracy
{
    public const int MinRuns = 20;

    public static Dictionary<string, ModelStats> Summarize(IEnumerable<RunOutcome> runs) =>
        runs.GroupBy(r => r.Model).ToDictionary(g => g.Key, g =>
        {
            var all = g.Count();
            var confirmed = g.Count(r => r.Confirmed);
            return new ModelStats(
                g.Key,
                all,
                confirmed,
                confirmed == 0 ? null : Math.Round((decimal)g.Count(r => r.Confirmed && r.Corrected) / confirmed, 3),
                Math.Round((decimal)g.Count(r => r.RatedDown) / all, 3),
                all >= MinRuns);
        });
}
