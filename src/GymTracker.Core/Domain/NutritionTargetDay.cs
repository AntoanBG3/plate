namespace GymTracker.Core.Domain;

/// <summary>The targets shown on one user's day, independent of later profile and maintenance changes.</summary>
public class NutritionTargetDay
{
    public string UserId { get; set; } = "";
    public DateOnly Date { get; set; }

    /// <summary>Serialized target values, including macro ranges, trend weight and maintenance.</summary>
    public string Values { get; set; } = "{}";
}
