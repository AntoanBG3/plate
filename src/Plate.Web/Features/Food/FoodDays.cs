using Plate.Core.Domain;
using Plate.Core.Time;
using Plate.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Features.Food;

/// <summary>Food can be logged for today and earlier, never a future day, and never before 2000 (SPEC 7 Food).</summary>
public static class FoodDays
{
    /// <summary>The first day food can be opened or logged; earlier dates only come from hand-typed or broken links.</summary>
    public static readonly DateOnly Earliest = new(2000, 1, 1);

    /// <summary>Whether a day can be opened: from <see cref="Earliest"/> to today.</summary>
    public static bool IsOpenable(DateOnly date, DateOnly today) => date >= Earliest && date <= today;

    public static async Task<DateOnly> TodayAsync(AppDbContext db, TimeProvider time, CancellationToken ct) =>
        UserClock.Today(time.GetUtcNow(), await db.Profiles.Select(p => p.TimeZoneId).SingleOrDefaultAsync(ct) ?? UserProfile.DefaultTimeZoneId);

    public static async Task EnsureNotFutureAsync(AppDbContext db, TimeProvider time, DateOnly date, CancellationToken ct)
    {
        if (date > await TodayAsync(db, time, ct))
        {
            throw new ValidationException("You can't log food for a future day.");
        }

        if (date < Earliest)
        {
            throw new ValidationException("That day is too far back.");
        }
    }
}
