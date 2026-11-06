namespace GymTracker.Core.Domain;

/// <summary>One successful food-add form intent and its original redirect, private to its account.</summary>
public sealed class FoodLogRequest
{
    public string UserId { get; set; } = "";
    public Guid RequestId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Location { get; set; } = "";
}
