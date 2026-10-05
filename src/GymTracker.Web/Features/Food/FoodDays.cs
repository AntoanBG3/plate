using GymTracker.Core.Domain;
using GymTracker.Core.Time;
using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Food;

/// <summary>Food can be logged for today and earlier, never a future day (SPEC 7 Food).</summary>
public static class FoodDays
{
    public static async Task<DateOnly> TodayAsync(AppDbContext db, TimeProvider time, CancellationToken ct) =>
        UserClock.Today(time.GetUtcNow(), await db.Profiles.Select(p => p.TimeZoneId).SingleOrDefaultAsync(ct) ?? UserProfile.DefaultTimeZoneId);

    public static async Task EnsureNotFutureAsync(AppDbContext db, TimeProvider time, DateOnly date, CancellationToken ct)
    {
        if (date > await TodayAsync(db, time, ct))
        {
            throw new ValidationException("You can't log food for a future day.");
        }
    }
}
