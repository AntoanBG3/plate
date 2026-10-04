namespace GymTracker.Web.Data;

public enum AdminVia
{
    Web,
    Cli,
}

/// <summary>One admin action: an unlock, a settings change or a write to feedback (SPEC 12). Never holds a secret.</summary>
public class AdminAudit
{
    public const int MaxDetailLength = 300;

    public long Id { get; set; }
    public DateTimeOffset At { get; set; }

    /// <summary>The signed-in account; null for the CLI, which runs as whoever has SSH access to wizard.</summary>
    public string? UserId { get; set; }

    public AdminVia Via { get; set; }
    public string Action { get; set; } = "";
    public string? Target { get; set; }
    public string? Ip { get; set; }
    public string? Detail { get; set; }
}
