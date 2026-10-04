namespace GymTracker.Web.Integrations;

/// <summary>The jobs AI models do in the app. Each has its own model setting, shared by both accounts.</summary>
public enum AiTask
{
    /// <summary>Reads typed food into items and grams (OpenRouter chat models).</summary>
    Parser,

    /// <summary>Picks which database food each parsed item is (decision models).</summary>
    Matcher,
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

    /// <summary>The parser keeps the setting name it had before the split, so a model already picked carries over.</summary>
    public static string SettingName(AiTask task) => task switch
    {
        AiTask.Parser => "OpenRouter:Model",
        AiTask.Matcher => "OpenRouter:Model:Matcher",
        _ => throw new ArgumentOutOfRangeException(nameof(task)),
    };

    /// <summary>The task named in a URL ("parser", "matcher"), or null for anything else.</summary>
    public static AiTask? FromSlug(string? slug) =>
        Enum.GetValues<AiTask>().Cast<AiTask?>().FirstOrDefault(t => string.Equals(Slug(t!.Value), slug, StringComparison.Ordinal));

    public static string Slug(AiTask task) => task.ToString().ToLowerInvariant();
}
