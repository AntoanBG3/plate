namespace GymTracker.Core.Domain;

/// <summary>A stored weekly summary, shown on Today for the week after the one it describes (SPEC 6.5).</summary>
public class WeeklySummary
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";

    /// <summary>The Monday of the week described.</summary>
    public DateOnly WeekStart { get; set; }

    public string Summary { get; set; } = "";
    public List<string> Tips { get; set; } = [];
    public string Motivation { get; set; } = "";

    /// <summary>The account's language when it was written; a change of language writes it again.</summary>
    public Language Language { get; set; }

    public string? Model { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
