using Plate.Core.Domain;

namespace Plate.Core.Nutrition;

/// <param name="Kcal">Null for a cut or bulk without a trend weight, since the adjustment depends on it.</param>
/// <param name="ProteinG">The protein aim; see <paramref name="Macros"/> for the minimum and the other targets.</param>
/// <param name="FloorApplied">The goal would have gone below the minimum, so the target was raised to it.</param>
/// <param name="Macros">Null without a calorie target or a trend weight.</param>
public record GoalTarget(int? Kcal, int? ProteinG, bool FloorApplied, MacroTarget? Macros = null);

/// <summary>Daily calorie and macro targets from the maintenance estimate and the goal (SPEC 5.6).</summary>
public static class GoalTargets
{
    public const int MinKcalMale = 1800;
    public const int MinKcalFemale = 1500;

    /// <summary>The minimum daily target. Without a stated sex, the lower limit applies.</summary>
    public static int MinKcal(Sex? sex) => sex == Sex.Male ? MinKcalMale : MinKcalFemale;

    /// <summary>kcal per day to remove (cut) or add (bulk) to lose or gain the target rate of trend weight per week.</summary>
    public static decimal DailyAdjustment(decimal trendKg, decimal ratePercentPerWeek) =>
        trendKg * ratePercentPerWeek / 100m * AdaptiveMaintenance.KcalPerKg / 7m;

    public static GoalTarget Calculate(int tdeeKcal, UserProfile profile, decimal? trendKg, DateOnly today)
    {
        decimal? target = profile.Goal switch
        {
            Goal.Maintain => tdeeKcal,
            Goal.Cut when CutCheck.OnBreak(profile, today) => tdeeKcal,
            Goal.Cut when trendKg is { } w => tdeeKcal - DailyAdjustment(w, profile.TargetRatePercentPerWeek),
            Goal.Bulk when trendKg is { } w => tdeeKcal + DailyAdjustment(w, profile.TargetRatePercentPerWeek),
            _ => null,
        };

        if (target is not { } t)
        {
            return new GoalTarget(null, MacroTargets.ProteinAim(profile, trendKg, today), false);
        }

        var kcal = (int)Math.Round(t, MidpointRounding.AwayFromZero);
        var min = MinKcal(profile.Sex);
        var floorApplied = kcal < min;
        if (floorApplied)
        {
            kcal = min;
        }

        var macros = MacroTargets.Calculate(kcal, profile, trendKg, today);
        return new GoalTarget(kcal, macros?.ProteinAimG, floorApplied, macros);
    }
}
