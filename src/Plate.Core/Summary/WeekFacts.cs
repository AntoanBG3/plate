using Plate.Core.Domain;
using Plate.Core.Nutrition;

namespace Plate.Core.Summary;

/// <summary>
/// The numbers a weekly summary is written from (SPEC 6.5): no name, email or notes. Exercise names include custom
/// exercises the user named.
/// </summary>
/// <param name="Monday">The first day of the week described.</param>
/// <param name="Food">Null with fewer than 3 logged days.</param>
/// <param name="Targets">Today's targets, standing in for the week's.</param>
/// <param name="Records">New e1RM records, as "Exercise: 102.5 kg (was 100)".</param>
/// <param name="TopMuscles">The most trained muscles by working sets, most first.</param>
public record WeekFacts(
    DateOnly Monday,
    Goal Goal,
    decimal RatePercentPerWeek,
    bool DietBreak,
    WeekSummary? Food,
    MacroTarget? Targets,
    (decimal Start, decimal End)? TrendKg,
    int WeighIns,
    int Sessions,
    int WorkingSets,
    IReadOnlyList<string> Records,
    IReadOnlyList<string> TopMuscles)
{
    public const int MinWeighIns = 3;

    public DateOnly Sunday => Monday.AddDays(6);

    /// <summary>Enough happened to write about: 3 days of food, a finished workout, or 3 weigh-ins.</summary>
    public bool HasEnough => Food is { LoggedDays: >= WeeklyNutrition.MinLoggedDays } || Sessions > 0 || WeighIns >= MinWeighIns;
}
