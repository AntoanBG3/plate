using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Profile;

/// <summary>Converts stored UTC times to the signed-in user's time zone for display.</summary>
public class UserTime(AppDbContext db)
{
    private TimeZoneInfo? zone;

    public async Task<DateTimeOffset> LocalAsync(DateTimeOffset utc)
    {
        zone ??= TimeZoneInfo.FindSystemTimeZoneById(
            await db.Profiles.Select(p => p.TimeZoneId).SingleOrDefaultAsync() ?? GymTracker.Core.Domain.UserProfile.DefaultTimeZoneId);
        return TimeZoneInfo.ConvertTime(utc, zone);
    }
}
