using Plate.Core.Domain;

namespace Plate.Core.Nutrition;

/// <summary>
/// What each activity level means, shown in Settings and given to the setup chat's model (SPEC 7, 6.4).
/// From NASEM 2023 table 7-1, which defines each level as activity on top of daily living (about 30 minutes of walking):
/// low active adds 60 to 80 minutes of brisk walking a day, active about two hours including some vigorous activity,
/// very active more than two hours of vigorous activity. The step counts are a rough conversion.
/// </summary>
public static class ActivityLevels
{
    public static string Description(ActivityLevel level) => level switch
    {
        ActivityLevel.Inactive => "Desk job and everyday errands, up to about 8,000 steps a day.",
        ActivityLevel.LowActive => "Everyday life plus about an hour of brisk walking or similar every day, roughly 11,000 to 14,000 steps.",
        ActivityLevel.Active => "Everyday life plus about two hours of activity a day, some of it hard (cycling, a sport), or a job on your feet with lifting and carrying.",
        ActivityLevel.VeryActive => "More than two hours of hard activity a day (running, cycling, sport), or heavy manual work plus training.",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    public static string Note => "Lifting 3 to 4 times a week counts for about half a level; add it to your walking. Used only until about three weeks of logs give an adaptive estimate.";
}
