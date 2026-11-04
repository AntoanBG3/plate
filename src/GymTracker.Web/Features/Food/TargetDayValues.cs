using System.Text.Json;
using GymTracker.Core.Nutrition;

namespace GymTracker.Web.Features.Food;

/// <summary>Only computed numbers are saved; past targets never consult the current profile.</summary>
public sealed record TargetDayValues(int MaintenanceKcal, GoalTarget Target, decimal? TrendWeightKg, int FloorKcal)
{
    public string ToJson() => JsonSerializer.Serialize(this);
    public static TargetDayValues Read(string json) => JsonSerializer.Deserialize<TargetDayValues>(json)
        ?? throw new InvalidOperationException("The stored nutrition targets are missing.");
}
