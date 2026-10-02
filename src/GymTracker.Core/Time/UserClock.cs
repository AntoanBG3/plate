namespace GymTracker.Core.Time;

public static class UserClock
{
    /// <summary>The calendar day it is for the user, in their time zone.</summary>
    public static DateOnly Today(DateTimeOffset now, string timeZoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
    }
}
