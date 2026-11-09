namespace Plate.Web.Features.Profile;

/// <summary>Converts stored UTC times to the signed-in user's time zone for display.</summary>
public class UserTime(ProfileService profiles)
{
    private TimeZoneInfo? zone;

    public async Task<DateTimeOffset> LocalAsync(DateTimeOffset utc)
    {
        zone ??= TimeZoneInfo.FindSystemTimeZoneById(
            (await profiles.FindAsync(CancellationToken.None))?.TimeZoneId ?? Plate.Core.Domain.UserProfile.DefaultTimeZoneId);
        return TimeZoneInfo.ConvertTime(utc, zone);
    }
}
