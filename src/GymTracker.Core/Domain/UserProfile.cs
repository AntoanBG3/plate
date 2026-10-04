namespace GymTracker.Core.Domain;

public class UserProfile
{
    public const string DefaultTimeZoneId = "Europe/Sofia";

    public string UserId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string TimeZoneId { get; set; } = DefaultTimeZoneId;
    public int? HeightCm { get; set; }
    public Sex? Sex { get; set; }
    public int? BirthYear { get; set; }

    /// <summary>Used only for the starting maintenance estimate; null counts as inactive.</summary>
    public ActivityLevel? ActivityLevel { get; set; }

    public int? InitialMaintenanceKcal { get; set; }
    public Goal Goal { get; set; } = Goal.Maintain;
    public decimal TargetRatePercentPerWeek { get; set; }
    public decimal ProteinGramsPerKg { get; set; } = 2.0m;
    public decimal DefaultWeightIncrementKg { get; set; } = 2.5m;
    public Theme Theme { get; set; } = Theme.Dark;
    public Language Language { get; set; } = Language.English;

    /// <summary>What's new entries up to this time have been dismissed.</summary>
    public DateTimeOffset? ChangelogSeenAt { get; set; }

    /// <summary>When the user finished or skipped the setup chat; null sends them to it (SPEC 6.4).</summary>
    public DateTimeOffset? OnboardedAt { get; set; }

    /// <summary>The plates the calculator offers by default: a common gym set.</summary>
    public static readonly decimal[] DefaultPlatesKg = [25m, 20m, 15m, 10m, 5m, 2.5m, 1.25m];

    /// <summary>Every plate Settings lets you tick.</summary>
    public static readonly decimal[] PlateChoicesKg = [25m, 20m, 15m, 10m, 5m, 2.5m, 2m, 1.25m, 1m, 0.5m];

    public decimal BarWeightKg { get; set; } = 20m;
    public List<decimal> PlatesKg { get; set; } = [.. DefaultPlatesKg];
}
