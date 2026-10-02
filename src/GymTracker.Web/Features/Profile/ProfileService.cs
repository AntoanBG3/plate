using GymTracker.Core.Domain;
using GymTracker.Core.Time;
using GymTracker.Web.Data;
using GymTracker.Web.Features;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Profile;

public class ProfileService(AppDbContext db, TimeProvider time)
{
    /// <summary>The current user's profile. Every user gets one when created, so this never returns null for a signed-in user.</summary>
    public async Task<UserProfile> GetAsync(CancellationToken ct) =>
        await db.Profiles.SingleAsync(ct);

    /// <summary>Saves the profile after checking it, including the SPEC 5.6 limits on cut and bulk rates.</summary>
    public async Task UpdateAsync(Action<UserProfile> change, CancellationToken ct)
    {
        var profile = await GetAsync(ct);
        change(profile);
        if (ProfileRules.Validate(profile, time.GetUtcNow().Year) is { } error)
        {
            throw new ValidationException(error);
        }

        await db.SaveChangesAsync(ct);
    }

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
