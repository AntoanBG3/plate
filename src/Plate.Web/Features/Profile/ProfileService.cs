using Plate.Core.Domain;
using Plate.Core.Time;
using Plate.Web.Data;
using Plate.Web.Features;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Features.Profile;

public class ProfileService(AppDbContext db, TimeProvider time)
{
    // Read once per request: the layout, the targets and the page all need it.
    private UserProfile? profile;

    /// <summary>The current user's profile. Every user gets one when created, so this never returns null for a signed-in user.</summary>
    public async Task<UserProfile> GetAsync(CancellationToken ct) =>
        await FindAsync(ct) ?? throw new InvalidOperationException("The signed-in user has no profile.");

    /// <summary>The current user's profile, or null when there is none (a session can outlive a deleted account).</summary>
    public async Task<UserProfile?> FindAsync(CancellationToken ct) =>
        profile ??= await db.Profiles.SingleOrDefaultAsync(ct);

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
