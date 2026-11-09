namespace Plate.Core.Domain;

/// <summary>
/// The setup chat in progress (SPEC 6.4), one per user, deleted when the user finishes or skips. The draft and the
/// messages are JSON so the chat survives closing the app.
/// </summary>
public class OnboardingChat
{
    public string UserId { get; set; } = "";

    /// <summary>The <see cref="Onboarding.OnboardingDraft"/> as JSON.</summary>
    public string Draft { get; set; } = "{}";

    /// <summary>The messages as a JSON array of <see cref="Onboarding.ChatLine"/>.</summary>
    public string Messages { get; set; } = "[]";

    /// <summary>Values refused on the last turn, as JSON, so the next turn can ask again.</summary>
    public string Rejected { get; set; } = "[]";

    /// <summary>The quick replies offered with the last message, as JSON.</summary>
    public string QuickReplies { get; set; } = "[]";

    public int Turns { get; set; }

    /// <summary>The model that answered last, so a bad conversation can be traced to a model.</summary>
    public string? Model { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
