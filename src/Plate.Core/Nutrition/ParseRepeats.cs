namespace Plate.Core.Nutrition;

/// <summary>
/// A parse the user typed again (SPEC 11, feedback eval): runs 20 to 22 were one Lidl meal typed three times while its
/// matching was being fixed, the earlier ones left with unticked rows. Scoring each as a separate meal counts the same
/// food several times and blames the earlier runs for food logged by the later one.
/// </summary>
public static class ParseRepeats
{
    /// <summary>How soon the meal must be typed again.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(3);

    /// <summary>Of the shorter text's words, the share the other must have.</summary>
    private const double Overlap = 0.6;

    private static readonly HashSet<string> Ignored = ["with", "from", "and", "the", "със", "около", "грама", "gram", "grams"];

    /// <summary>
    /// The later parse replaces the earlier one: the same account typed mostly the same words again within the window,
    /// and the earlier one had something the user didn't accept (a meal eaten twice is usually accepted both times).
    /// </summary>
    public static bool Supersedes(DateTimeOffset earlierConfirmed, string earlierText, bool earlierAccepted, DateTimeOffset laterCreated, string laterText)
    {
        if (earlierAccepted || laterCreated < earlierConfirmed || laterCreated - earlierConfirmed > Window)
        {
            return false;
        }

        var a = Words(earlierText);
        var b = Words(laterText);
        return a.Count > 0 && b.Count > 0 && (double)a.Intersect(b).Count() / Math.Min(a.Count, b.Count) >= Overlap;
    }

    /// <summary>Lower-case words of three or more letters, without the commonest filler.</summary>
    private static HashSet<string> Words(string text) =>
        text.ToLowerInvariant()
            .Split(text.Where(c => !char.IsLetter(c)).Distinct().ToArray(), StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !Ignored.Contains(w))
            .ToHashSet();
}
