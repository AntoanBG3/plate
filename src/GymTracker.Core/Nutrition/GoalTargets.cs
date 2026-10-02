using GymTracker.Core.Domain;

namespace GymTracker.Core.Nutrition;

/// <param name="Kcal">Null for a cut or bulk without a trend weight, since the adjustment depends on it.</param>
/// <param name="FloorApplied">The goal would have gone below the minimum, so the target was raised to it.</param>
public record GoalTarget(int? Kcal, int? ProteinG, bool FloorApplied);

/// <summary>Daily calorie and protein targets from the maintenance estimate and the goal (SPEC 5.6).</summary>
public static class GoalTargets
{
    public const int MinKcalMale = 1800;
    public const int MinKcalFemale = 1500;

    /// <summary>The minimum daily target. Without a stated sex, the lower limit applies.</summary>
    public static int MinKcal(Sex? sex) => sex == Sex.Male ? MinKcalMale : MinKcalFemale;

    /// <summary>kcal per day to remove (cut) or add (bulk) to lose or gain the target rate of trend weight per week.</summary>
    public static decimal DailyAdjustment(decimal trendKg, decimal ratePercentPerWeek) =>
        trendKg * ratePercentPerWeek / 100m * AdaptiveMaintenance.KcalPerKg / 7m;

    public static GoalTarget Calculate(int tdeeKcal, UserProfile profile, decimal? trendKg)
    {
        var protein = Maintenance.ProteinTarget(profile, trendKg);
        decimal? target = profile.Goal switch
        {
            Goal.Maintain => tdeeKcal,
            Goal.Cut when trendKg is { } w => tdeeKcal - DailyAdjustment(w, profile.TargetRatePercentPerWeek),
            Goal.Bulk when trendKg is { } w => tdeeKcal + DailyAdjustment(w, profile.TargetRatePercentPerWeek),
            _ => null,
        };

        if (target is not { } t)
        {
            return new GoalTarget(null, protein, false);
        }

        var kcal = (int)Math.Round(t, MidpointRounding.AwayFromZero);
        var min = MinKcal(profile.Sex);
        return kcal < min ? new GoalTarget(min, protein, true) : new GoalTarget(kcal, protein, false);
    }
}
