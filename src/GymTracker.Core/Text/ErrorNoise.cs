namespace GymTracker.Core.Text;

/// <summary>
/// Browser messages that aren't the app's errors. A page transition is skipped whenever the document is hidden or the
/// viewport changes mid-way (the phone locks, the keyboard opens); the browser reports that as a rejected promise.
/// </summary>
public static class ErrorNoise
{
    private static readonly string[] Ignored =
    [
        "Transition was aborted",
        "Transition was skipped",
        "Skipping view transition",
        "view transition aborted", // "Old view transition aborted by new view transition."
    ];

    public static bool IsIgnored(string? message) =>
        message is not null && Ignored.Any(i => message.Contains(i, StringComparison.OrdinalIgnoreCase));
}
