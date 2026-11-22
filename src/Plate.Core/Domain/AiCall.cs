namespace Plate.Core.Domain;

/// <summary>
/// One OpenRouter call made for an account, with what OpenRouter says it cost (SPEC 11). Counts, time and money only:
/// never the text sent or received. Recorded since the table was added; earlier calls are unknown, not free.
/// </summary>
public class AiCall
{
    public const int MaxTaskLength = 20;
    public const int MaxModelLength = 200;

    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public DateTimeOffset At { get; set; }

    /// <summary>"parser", "matcher", "namer", "onboarding", "weekly" or "label".</summary>
    public string Task { get; set; } = "";

    /// <summary>The model OpenRouter answered with, else the one asked for; null when neither is known.</summary>
    public string? Model { get; set; }

    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }

    /// <summary>US dollars, from the answer's usage block; null when it gave none (a failed call).</summary>
    public decimal? Cost { get; set; }

    public int DurationMs { get; set; }

    /// <summary>No answer, or an error status: a timeout, a refusal, an outage.</summary>
    public bool Failed { get; set; }
}
