using Plate.Core.Nutrition;
using Plate.Web.Data;
using Plate.Web.Features.Food;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Plate.Web.Features.Feedback;

/// <summary>
/// An account's usual foods and last amounts as they stood at one moment, for replaying an old parse (SPEC 11, feedback
/// eval): nothing learned after the meal may help the replay. Last amounts come from entries made before then. Usual
/// foods are rebuilt from the parses confirmed before then, the way confirming writes them (the latest save of the
/// words wins), since the stored rows only keep the latest food; words not used for the usual-food retention before then
/// are forgotten, as the cleanup job would have.
/// </summary>
public sealed class HistoryAsOf(AppDbContext db, ICurrentUser currentUser, DateTimeOffset asOf)
    : RepeatLoggingService(db, currentUser, new FixedTime(asOf), NullLogger<RepeatLoggingService>.Instance)
{
    /// <summary>Usual foods unused this long are removed (SPEC 11, retention).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(180);

    // Assigned rather than captured: the base class captures db as well, and two captures warn (CS9107).
    private readonly AppDbContext dbContext = db;
    private Dictionary<string, int>? usual;

    protected override async Task<Dictionary<string, int>> UsualFoodIdsAsync(IReadOnlyCollection<string> phrases, CancellationToken ct)
    {
        usual ??= await RebuildAsync(ct);
        return phrases.Where(usual.ContainsKey).ToDictionary(p => p, p => usual[p]);
    }

    private async Task<Dictionary<string, int>> RebuildAsync(CancellationToken ct)
    {
        var since = asOf - Retention;
        var runs = await dbContext.ParseRuns.AsNoTracking() // the owner filter keeps this to the account
            .Where(r => r.ConfirmedAt != null && r.ConfirmedAt < asOf && r.ConfirmedAt >= since && r.Saved != null)
            .OrderBy(r => r.ConfirmedAt)
            .Select(r => new { r.Parsed, r.Saved })
            .ToListAsync(ct);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            var parsed = FeedbackJson.Read<List<ParsedFoodItem>>(run.Parsed) ?? [];
            var saved = FeedbackJson.Read<List<SavedRow>>(run.Saved) ?? [];
            for (var n = 0; n < Math.Min(parsed.Count, saved.Count); n++)
            {
                if (saved[n] is { Included: true, FoodItemId: { } food } && UsualFoods.Key(parsed[n].NameOriginal) is { } key)
                {
                    result[key] = food;
                }
            }
        }

        return result;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
