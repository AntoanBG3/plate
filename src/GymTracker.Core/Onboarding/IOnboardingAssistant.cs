using GymTracker.Core.Text;

namespace GymTracker.Core.Onboarding;

/// <summary>One message in the setup chat.</summary>
public sealed record ChatLine(bool FromUser, string Text);

/// <summary>Everything the model sees on one turn. The draft, not the model's memory, is the record of what is known.</summary>
/// <param name="Rejected">Values from the last turn that were outside the limits, so the model can ask again.</param>
public sealed record OnboardingContext(
    OnboardingDraft Draft,
    IReadOnlyList<OnboardingField> Missing,
    IReadOnlyList<OnboardingRejection> Rejected,
    IReadOnlyList<ChatLine> History,
    bool Bulgarian,
    int CurrentYear);

/// <param name="Reply">What the assistant says next; null when the call failed.</param>
/// <param name="QuickReplies">Up to four short answers the user can tap.</param>
public sealed record OnboardingTurn(string? Reply, OnboardingFields Fields, IReadOnlyList<string> QuickReplies, LocalText? Error, string? Model)
{
    public static OnboardingTurn Failed(LocalText error) => new(null, OnboardingFields.None, [], error, null);
}

/// <summary>The model behind the setup chat (SPEC 6.4): reads the user's answers and proposes profile values.</summary>
public interface IOnboardingAssistant
{
    /// <summary>False when there is no model to talk to; the setup page then shows only the form.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct);

    Task<OnboardingTurn> NextAsync(OnboardingContext context, CancellationToken ct);
}
