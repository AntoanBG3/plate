using GymTracker.Core.Domain;
using GymTracker.Core.Time;
using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Profile;

public class ProfileService(AppDbContext db, TimeProvider time)
{
    /// <summary>The current user's profile. Every user gets one when created, so this never returns null for a signed-in user.</summary>
    public async Task<UserProfile> GetAsync(CancellationToken ct) =>
        await db.Profiles.SingleAsync(ct);

    public async Task<DateOnly> TodayAsync(CancellationToken ct)
    {
        var profile = await GetAsync(ct);
        return UserClock.Today(time.GetUtcNow(), profile.TimeZoneId);
    }
}

/// <summary>Small read-only profile lookups used by other services.</summary>
public class ProfileLookup(AppDbContext db)
{
    public async Task<decimal> DefaultIncrementAsync(CancellationToken ct) =>
        await db.Profiles.Select(p => (decimal?)p.DefaultWeightIncrementKg).SingleOrDefaultAsync(ct) ?? 2.5m;
}
