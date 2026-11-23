namespace Plate.Web.Integrations;

/// <summary>The jobs AI models do in the app. Each has its own model setting, shared by all accounts.</summary>
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

    /// <summary>Reads nutrition labels from photos (OpenRouter vision models, SPEC 6.6).</summary>
    Label,

    /// <summary>Estimates values per 100 g for foods with no database match (OpenRouter chat models, SPEC 6.1 step 6).</summary>
    Estimator,
}

public static class AiTasks
{
    /// <summary>The effort setting's value for "the model's lowest", the default; stored as no setting.</summary>
    public const string LowestEffort = "lowest";

    /// <summary>The tasks with an effort setting: the chat models. The matcher's decision models have none.</summary>
    public static bool HasEffort(AiTask task) => task != AiTask.Matcher;

    /// <summary>Where a task's reasoning effort is stored in AppSettings.</summary>
    public static string EffortSettingName(AiTask task) => $"OpenRouter:Effort:{Slug(task)}";

    /// <summary>The matcher's "model" for no model: the database ranking alone.</summary>
    public const string Off = "off";

    /// <summary>The matcher can be turned off, which leaves the database ranking alone.</summary>
    public static readonly OpenRouterModel MatcherOff = new(Off, "Plate", "Off: database ranking only", 0, 0, false, null);

    /// <summary>How many of the most used chat models a picker shows.</summary>
    public const int PickerSize = 40;

    /// <summary>Models older than this are left out of the pickers.</summary>
    public static readonly TimeSpan PickerMaxAge = TimeSpan.FromDays(365);

    /// <summary>
    /// The models a picker shows, in the list's order: no aliases, none OpenRouter is retiring, none older than a
    /// year, and with a <paramref name="limit"/> only that many of the most used last week. Off and the current
    /// model always stay, so a pick made before still shows as picked.
    /// </summary>
    public static IReadOnlyList<OpenRouterModel> ForPicker(IReadOnlyList<OpenRouterModel> models, string current, DateTimeOffset now, int? limit)
    {
        var recent = models.Where(m => m.Id != Off && !m.Id.StartsWith('~') && m.Expires is null
            && (m.Created is not { } created || now - created <= PickerMaxAge));
        var kept = (limit is { } n ? recent.OrderBy(m => m.Rank ?? int.MaxValue).Take(n) : recent).Select(m => m.Id).ToHashSet();
        return models.Where(m => m.Id == Off || m.Id == current || kept.Contains(m.Id)).ToList();
    }

    /// <summary>The setup chat can be turned off, which leaves the form.</summary>
    public static readonly OpenRouterModel OnboardingOff = new(Off, "Plate", "Off: setup form only", 0, 0, false, null);

    /// <summary>The weekly summary can be turned off for every account.</summary>
    public static readonly OpenRouterModel WeeklyOff = new(Off, "Plate", "Off: no weekly summary", 0, 0, false, null);

    /// <summary>Label photos can be turned off for every account.</summary>
    public static readonly OpenRouterModel LabelOff = new(Off, "Plate", "Off: no label photos", 0, 0, false, null);

    /// <summary>The estimator can be turned off, which leaves the parser's own guesses on unmatched foods.</summary>
    public static readonly OpenRouterModel EstimatorOff = new(Off, "Plate", "Off: the parser's own estimates", 0, 0, false, null);

    /// <summary>The label model until one is picked: a cheap vision model that reads Cyrillic well (SPEC 6.6).</summary>
    public const string DefaultLabelModel = "google/gemini-3.5-flash-lite";

    /// <summary>English name of a task, for the admin area and the CLI.</summary>
    public static string Title(AiTask task) => task switch
    {
        AiTask.Parser => "Food parser",
        AiTask.Matcher => "Food matcher",
        AiTask.Onboarding => "Setup chat",
        AiTask.Label => "Label reader",
        AiTask.Estimator => "Food estimator",
        _ => "Weekly summary",
    };

    /// <summary>What "off" means for a task.</summary>
    public static string OffLabel(AiTask task) => task switch
    {
        AiTask.Onboarding => "Off: setup form only",
        AiTask.Weekly => "Off: no weekly summary",
        AiTask.Label => "Off: no label photos",
        AiTask.Estimator => "Off: the parser's own estimates",
        _ => "Off: database ranking only",
    };

    /// <summary>The parser keeps the setting name it had before the split, so a model already picked carries over.</summary>
    public static string SettingName(AiTask task) => task switch
    {
        AiTask.Parser => "OpenRouter:Model",
        AiTask.Matcher => "OpenRouter:Model:Matcher",
        AiTask.Onboarding => "OpenRouter:Model:Onboarding",
        AiTask.Weekly => "OpenRouter:Model:Weekly",
        AiTask.Label => "OpenRouter:Model:Label",
        AiTask.Estimator => "OpenRouter:Model:Estimator",
        _ => throw new ArgumentOutOfRangeException(nameof(task)),
    };

    /// <summary>The task named in a URL ("parser", "matcher"), or null for anything else.</summary>
    public static AiTask? FromSlug(string? slug) =>
        Enum.GetValues<AiTask>().Cast<AiTask?>().FirstOrDefault(t => string.Equals(Slug(t!.Value), slug, StringComparison.Ordinal));

    public static string Slug(AiTask task) => task.ToString().ToLowerInvariant();
}
