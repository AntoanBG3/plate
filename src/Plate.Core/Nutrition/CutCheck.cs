using Plate.Core.Domain;

namespace Plate.Core.Nutrition;

public enum CutCheckKind
{
    /// <summary>Losing more than 1% a week and more than 1.5 times the target rate.</summary>
    TooFast,

    /// <summary>The weekly protein average is under the minimum.</summary>
    ProteinLow,

    /// <summary>12 weeks into a cut: offer a diet break.</summary>
    BreakOffer,
}

/// <param name="LossPercentPerWeek">For <see cref="CutCheckKind.TooFast"/>.</param>
/// <param name="ProteinG">For <see cref="CutCheckKind.ProteinLow"/>: the weekly average.</param>
/// <param name="ProteinMinG">For <see cref="CutCheckKind.ProteinLow"/>: the minimum it is under.</param>
public record CutCheckIn(CutCheckKind Kind, decimal? LossPercentPerWeek = null, decimal? ProteinG = null, int? ProteinMinG = null);

/// <summary>Check-ins during a cut and the diet break that pauses it (SPEC 5.9).</summary>
public static class CutCheck
{
    public const decimal FastLossPercentPerWeek = 1.0m;
    public const decimal FastLossTimesTarget = 1.5m;
    public const int PaceDays = 14;
    public const int BreakAfterDays = 84;
    public const int BreakDays = 14;
    public const int DismissDays = 7;

    public static bool OnBreak(UserProfile p, DateOnly today) =>
        p.Goal == Goal.Cut && p.DietBreakUntil is { } until && today <= until;

    /// <summary>The last day of a break that starts today.</summary>
    public static DateOnly BreakUntil(DateOnly today) => today.AddDays(BreakDays - 1);

    /// <summary>The last day a dismissal made today hides check-ins.</summary>
    public static DateOnly DismissedUntil(DateOnly today) => today.AddDays(DismissDays - 1);

    public static IReadOnlyList<CutCheckIn> For(UserProfile p, DateOnly today, IReadOnlyList<WeighIn> weighIns, WeekSummary? week, MacroTarget? targets)
    {
        if (p.Goal != Goal.Cut || OnBreak(p, today) || p.CutCheckDismissedUntil is { } hidden && today <= hidden)
        {
            return [];
        }

        var checks = new List<CutCheckIn>();
        if (TrendWeight.On(weighIns, today) is { } now && TrendWeight.On(weighIns, today.AddDays(-PaceDays)) is { } before && before > 0)
        {
            var lossPerWeek = (before - now) / before * 100m / (PaceDays / 7m);
            if (lossPerWeek > FastLossPercentPerWeek && lossPerWeek > FastLossTimesTarget * p.TargetRatePercentPerWeek)
            {
                checks.Add(new CutCheckIn(CutCheckKind.TooFast, LossPercentPerWeek: Math.Round(lossPerWeek, 1)));
            }
        }

        if (week is not null && targets is not null && week.ProteinG < targets.ProteinMinG)
        {
            checks.Add(new CutCheckIn(CutCheckKind.ProteinLow, ProteinG: week.ProteinG, ProteinMinG: targets.ProteinMinG));
        }

        if (p.CutStartedOn is { } started && today.DayNumber - started.DayNumber >= BreakAfterDays)
        {
            checks.Add(new CutCheckIn(CutCheckKind.BreakOffer));
        }

        return checks;
    }

    /// <summary>
    /// Keeps the cut dates current: a cut without a start date starts today, and the day after a break ends the cut
    /// restarts. Returns whether anything changed.
    /// </summary>
    public static bool Sync(UserProfile p, DateOnly today)
    {
        if (p.Goal != Goal.Cut)
        {
            return false;
        }

        if (p.DietBreakUntil is { } until && today > until)
        {
            (p.CutStartedOn, p.DietBreakUntil) = (until.AddDays(1), null);
            return true;
        }

        if (p.CutStartedOn is null)
        {
            p.CutStartedOn = today;
            return true;
        }

        return false;
    }

    public static void StartBreak(UserProfile p, DateOnly today) => p.DietBreakUntil = BreakUntil(today);

    public static void EndBreak(UserProfile p, DateOnly today) => (p.CutStartedOn, p.DietBreakUntil) = (today, null);

    /// <summary>After a goal change: a new cut starts today; leaving a cut clears its dates.</summary>
    public static void GoalChanged(UserProfile p, Goal previous, DateOnly today)
    {
        if (p.Goal != Goal.Cut)
        {
            (p.CutStartedOn, p.DietBreakUntil) = (null, null);
        }
        else if (previous != Goal.Cut || p.CutStartedOn is null)
        {
            p.CutStartedOn = today;
        }
    }
}
