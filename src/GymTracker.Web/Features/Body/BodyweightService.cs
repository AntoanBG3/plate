using GymTracker.Core.Text;
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Data;
using GymTracker.Web.Features;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Body;

public class BodyweightService
{
    private readonly AppDbContext db;
    private readonly ICurrentUser currentUser;

    // Every weigh-in, read once per request: Today needs the day's entry, the latest, the trend and the chart.
    // Any save on the context clears it, so a write earlier in the same scope is never hidden.
    private List<BodyweightEntry>? all;

    public BodyweightService(AppDbContext db, ICurrentUser currentUser)
    {
        this.db = db;
        this.currentUser = currentUser;
        db.SavedChanges += (_, _) => all = null;
    }

    private async Task<IReadOnlyList<BodyweightEntry>> AllAsync(CancellationToken ct) =>
        all ??= await db.Bodyweights.AsNoTracking().OrderBy(b => b.Date).ToListAsync(ct);

    public async Task<IReadOnlyList<WeighIn>> WeighInsAsync(CancellationToken ct) =>
        (await AllAsync(ct)).Select(b => new WeighIn(b.Date, b.WeightKg)).ToList();

    public async Task<BodyweightEntry?> GetForDayAsync(DateOnly date, CancellationToken ct) =>
        (await AllAsync(ct)).LastOrDefault(b => b.Date == date);

    public async Task<BodyweightEntry?> LatestAsync(CancellationToken ct) =>
        (await AllAsync(ct)).LastOrDefault();

    /// <summary>Records the weight for a day. A second entry on the same day replaces the first.</summary>
    public async Task<BodyweightEntry> SetAsync(DateOnly date, decimal weightKg, CancellationToken ct)
    {
        if (ProfileRules.WeightError(weightKg) is { } error)
        {
            throw new ValidationException(error);
        }

        var entry = await db.Bodyweights.SingleOrDefaultAsync(b => b.Date == date, ct);
        if (entry is null)
        {
            entry = new BodyweightEntry { UserId = RequireUserId(), Date = date };
            db.Bodyweights.Add(entry);
        }

        entry.WeightKg = decimal.Round(weightKg, 2);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    private string RequireUserId() =>
        currentUser.UserId ?? throw new InvalidOperationException("No signed-in user.");
}
