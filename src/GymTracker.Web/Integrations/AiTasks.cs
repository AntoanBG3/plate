namespace GymTracker.Web.Integrations;

/// <summary>The jobs AI models do in the app. Each has its own model setting, shared by both accounts.</summary>
public enum AiTask
{
    /// <summary>Reads typed food into items and grams (OpenRouter chat models).</summary>
    Parser,

    /// <summary>Picks which database food each parsed item is (decision models).</summary>
    Matcher,

    /// <summary>Runs the first sign-in setup chat (OpenRouter chat models, SPEC 6.4).</summary>
    Onboarding,

    /// <summary>Writes the weekly summary on Today (OpenRouter chat models, SPEC 6.5).</summary>
    Weekly,
}

public static class AiTasks
{
    /// <summary>The matcher's "model" for no model: the database ranking alone.</summary>
    public const string Off = "off";

    /// <summary>
    /// Decision models are not in OpenRouter's model list, and the matcher's thresholds are measured per version,
    /// so only versions evaluated for food matching are offered.
    /// </summary>
    public static readonly IReadOnlyList<OpenRouterModel> MatcherModels =
    [
        new(Off, "GymTracker", "Off: database ranking only", 0, 0, false, null),
        new(JevFoodMatchDecider.Jev113, "TypeSafe", "Jev 1.13", 0.042m, 0, false, null),
    ];

    /// <summary>The setup chat can be turned off, which leaves the form.</summary>
    public static readonly OpenRouterModel OnboardingOff = new(Off, "GymTracker", "Off: setup form only", 0, 0, false, null);

    /// <summary>The weekly summary can be turned off for both accounts.</summary>
    public static readonly OpenRouterModel WeeklyOff = new(Off, "GymTracker", "Off: no weekly summary", 0, 0, false, null);

    /// <summary>English name of a task, for the admin area and the CLI.</summary>
    public static string Title(AiTask task) => task switch
    {
        AiTask.Parser => "Food parser",
        AiTask.Matcher => "Food matcher",
        AiTask.Onboarding => "Setup chat",
        _ => "Weekly summary",
    };

    /// <summary>What "off" means for a task.</summary>
    public static string OffLabel(AiTask task) => task switch
    {
        AiTask.Onboarding => "Off: setup form only",
        AiTask.Weekly => "Off: no weekly summary",
        _ => "Off: database ranking only",
    };

    /// <summary>The parser keeps the setting name it had before the split, so a model already picked carries over.</summary>
    public static string SettingName(AiTask task) => task switch
    {
        AiTask.Parser => "OpenRouter:Model",
        AiTask.Matcher => "OpenRouter:Model:Matcher",
        AiTask.Onboarding => "OpenRouter:Model:Onboarding",
        AiTask.Weekly => "OpenRouter:Model:Weekly",
        _ => throw new ArgumentOutOfRangeException(nameof(task)),
    };

    /// <summary>The task named in a URL ("parser", "matcher"), or null for anything else.</summary>
    public static AiTask? FromSlug(string? slug) =>
        Enum.GetValues<AiTask>().Cast<AiTask?>().FirstOrDefault(t => string.Equals(Slug(t!.Value), slug, StringComparison.Ordinal));

    public static string Slug(AiTask task) => task.ToString().ToLowerInvariant();
}
