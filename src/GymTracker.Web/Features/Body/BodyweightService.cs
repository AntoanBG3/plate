using GymTracker.Core.Text;
using GymTracker.Core.Domain;
using GymTracker.Web.Data;
using GymTracker.Web.Features;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Body;

public class BodyweightService(AppDbContext db, ICurrentUser currentUser)
{
    public const decimal MinWeightKg = 20m;
    public const decimal MaxWeightKg = 400m;

    public Task<BodyweightEntry?> GetForDayAsync(DateOnly date, CancellationToken ct) =>
        db.Bodyweights.SingleOrDefaultAsync(b => b.Date == date, ct);

    public Task<BodyweightEntry?> LatestAsync(CancellationToken ct) =>
        db.Bodyweights.OrderByDescending(b => b.Date).FirstOrDefaultAsync(ct);

    /// <summary>Records the weight for a day. A second entry on the same day replaces the first.</summary>
    public async Task<BodyweightEntry> SetAsync(DateOnly date, decimal weightKg, CancellationToken ct)
    {
        if (weightKg < MinWeightKg || weightKg > MaxWeightKg)
        {
            throw new ValidationException(new LocalText("Weight must be between {0} and {1} kg.", MinWeightKg, MaxWeightKg));
        }

        var entry = await GetForDayAsync(date, ct);
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
