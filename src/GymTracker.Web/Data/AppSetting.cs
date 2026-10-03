namespace GymTracker.Web.Data;

/// <summary>An app-wide setting shared by every account, such as the OpenRouter API key (stored encrypted) and model.</summary>
public class AppSetting
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedByUserId { get; set; }
}
