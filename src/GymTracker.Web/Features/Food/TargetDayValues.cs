using System.Text.Json;
using System.Text.Json.Serialization;
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;

namespace GymTracker.Web.Features.Food;

/// <summary>Only computed numbers are saved; past targets never consult the current profile.</summary>
public sealed record TargetDayValues(int MaintenanceKcal, GoalTarget Target, decimal? TrendWeightKg, int FloorKcal)
{
    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    /// <summary>
    /// A day before snapshots existed: calories and protein as its estimate stored them, and the fat, carb and fibre
    /// ranges for those two numbers from <paramref name="profile"/>. The protein aim is the recorded one, so the Food
    /// page, the charts and the weekly summary all show the same protein for that day.
    /// </summary>
    public static TargetDayValues Legacy(MaintenanceEstimate estimate, UserProfile profile, decimal? trendKg, DateOnly day)
    {
        var floor = GoalTargets.MinKcal(profile.Sex);
        var macros = estimate.TargetKcal is { } kcal
            ? MacroTargets.Calculate(kcal, profile, trendKg, day, estimate.TargetProteinG)
            : null;
        return new(estimate.EstimatedTdeeKcal, new GoalTarget(estimate.TargetKcal, estimate.TargetProteinG, estimate.TargetKcal == floor, macros), trendKg, floor);
    }

    // Rows are written once a day and never rewritten, so the stored names are fixed here instead of following the
    // Core records: renaming a property in GoalTarget or MacroTarget must not silently turn history into zeros.
    // A new MacroTarget field fails to compile below until it is given a stored name. TargetDayValuesTests pins the JSON.
    public string ToJson() => JsonSerializer.Serialize(new Stored(
        MaintenanceKcal, Target.Kcal, Target.ProteinG, Target.FloorApplied, FloorKcal, TrendWeightKg,
        Target.Macros is { } m
            ? new StoredMacros(m.ReferenceKg, m.ProteinMinG, m.ProteinAimG, m.FatMinG, m.FatMaxG, m.CarbsMinG, m.CarbsMaxG,
                m.FiberMinG, m.SaturatedFatMaxG, m.MealProteinG, m.ProteinLowered)
            : null), Options);

    public static TargetDayValues Read(string json)
    {
        var s = JsonSerializer.Deserialize<Stored>(json, Options) ?? throw new InvalidOperationException("The stored nutrition targets are missing.");
        var macros = s.Macros is { } m
            ? new MacroTarget(m.ReferenceKg, m.ProteinMinG, m.ProteinAimG, m.FatMinG, m.FatMaxG, m.CarbsMinG, m.CarbsMaxG,
                m.FiberMinG, m.SaturatedFatMaxG, m.MealProteinG, m.ProteinLowered)
            : null;
        return new(s.MaintenanceKcal, new GoalTarget(s.Kcal, s.ProteinG, s.FloorApplied, macros), s.TrendWeightKg, s.FloorKcal);
    }

    private sealed record Stored(
        [property: JsonPropertyName("maintenanceKcal"), JsonRequired] int MaintenanceKcal,
        [property: JsonPropertyName("kcal")] int? Kcal,
        [property: JsonPropertyName("proteinG")] int? ProteinG,
        [property: JsonPropertyName("floorApplied"), JsonRequired] bool FloorApplied,
        [property: JsonPropertyName("floorKcal"), JsonRequired] int FloorKcal,
        [property: JsonPropertyName("trendWeightKg")] decimal? TrendWeightKg,
        [property: JsonPropertyName("macros")] StoredMacros? Macros);

    private sealed record StoredMacros(
        [property: JsonPropertyName("referenceKg"), JsonRequired] decimal ReferenceKg,
        [property: JsonPropertyName("proteinMinG"), JsonRequired] int ProteinMinG,
        [property: JsonPropertyName("proteinAimG"), JsonRequired] int ProteinAimG,
        [property: JsonPropertyName("fatMinG"), JsonRequired] int FatMinG,
        [property: JsonPropertyName("fatMaxG"), JsonRequired] int FatMaxG,
        [property: JsonPropertyName("carbsMinG"), JsonRequired] int CarbsMinG,
        [property: JsonPropertyName("carbsMaxG"), JsonRequired] int CarbsMaxG,
        [property: JsonPropertyName("fiberMinG"), JsonRequired] int FiberMinG,
        [property: JsonPropertyName("saturatedFatMaxG"), JsonRequired] int SaturatedFatMaxG,
        [property: JsonPropertyName("mealProteinG"), JsonRequired] int MealProteinG,
        [property: JsonPropertyName("proteinLowered"), JsonRequired] bool ProteinLowered);
}
