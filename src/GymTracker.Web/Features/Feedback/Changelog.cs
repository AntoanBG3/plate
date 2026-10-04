using GymTracker.Core.Domain;
using GymTracker.Web.Data;
using GymTracker.Web.Features.Localization;
using GymTracker.Web.Features.Profile;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Feedback;

/// <param name="Text">In the viewer's language.</param>
/// <param name="Yours">The entry answers a report the viewer sent.</param>
public record WhatsNewItem(string Text, bool Yours);

/// <summary>What's new on Today: changelog entries the user hasn't dismissed (SPEC 11).</summary>
public class Changelog(AppDbContext db, ProfileService profiles, TimeProvider time)
{
    /// <summary>A new account sees only the last two weeks, not the whole history.</summary>
    private static readonly TimeSpan FirstLook = TimeSpan.FromDays(14);

    public async Task<List<WhatsNewItem>> UnseenAsync(CancellationToken ct)
    {
        var profile = await profiles.GetAsync(ct);
        var since = profile.ChangelogSeenAt ?? time.GetUtcNow() - FirstLook;
        var entries = await db.Changelog.AsNoTracking()
            .Where(e => e.CreatedAt > since)
            .OrderBy(e => e.CreatedAt)
            .Take(10)
            .Select(e => new { e.TextEn, e.TextBg, e.FeedbackId })
            .ToListAsync(ct);
        if (entries.Count == 0)
        {
            return [];
        }

        // Through the user filter: only the viewer's own reports count.
        var linked = entries.Where(e => e.FeedbackId is not null).Select(e => e.FeedbackId!.Value).ToList();
        var mine = await db.Feedback.Where(f => linked.Contains(f.Id)).Select(f => f.Id).ToListAsync(ct);
        return entries.Select(e => new WhatsNewItem(Localizer.IsBulgarian ? e.TextBg : e.TextEn, e.FeedbackId is { } id && mine.Contains(id))).ToList();
    }

    /// <summary>Written directly, so a profile that fails today's validation can still dismiss the card.</summary>
    public Task DismissAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return db.Profiles.ExecuteUpdateAsync(u => u.SetProperty(p => p.ChangelogSeenAt, now), ct);
    }
}
