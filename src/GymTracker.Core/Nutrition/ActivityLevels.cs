using GymTracker.Core.Domain;

namespace GymTracker.Core.Nutrition;

/// <summary>
/// What each activity level means, shown in Settings and given to the setup chat's model (SPEC 7, 6.4).
/// From the NASEM 2023 examples; the step counts are a rough guide.
/// </summary>
public static class ActivityLevels
{
    public static string Description(ActivityLevel level) => level switch
    {
        ActivityLevel.Inactive => "Desk job, up to about 7,500 steps a day, little other exercise.",
        ActivityLevel.LowActive => "About 7,500 to 12,000 steps a day, or an hour of exercise most days.",
        ActivityLevel.Active => "A physical job, or 1.5 to 2 hours of moderate activity a day.",
        ActivityLevel.VeryActive => "Two hours or more of moderate to hard activity a day: manual work plus training, endurance athletes.",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    public static string Note => "Lifting 3 to 4 times a week rarely moves you up a level on its own; daily movement matters more. Used only until about three weeks of logs give an adaptive estimate.";
}
