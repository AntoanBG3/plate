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
    public int? InitialMaintenanceKcal { get; set; }
    public Goal Goal { get; set; } = Goal.Maintain;
    public decimal TargetRatePercentPerWeek { get; set; }
    public decimal ProteinGramsPerKg { get; set; } = 2.0m;
    public decimal DefaultWeightIncrementKg { get; set; } = 2.5m;
    public Theme Theme { get; set; } = Theme.Dark;
    public Language Language { get; set; } = Language.English;
}
