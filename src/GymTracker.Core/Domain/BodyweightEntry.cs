namespace GymTracker.Core.Domain;

public class BodyweightEntry
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";

    /// <summary>The day in the user's time zone. One entry per user and day.</summary>
    public DateOnly Date { get; set; }

    public decimal WeightKg { get; set; }
}
