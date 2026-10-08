using GymTracker.Core.Domain;
using GymTracker.Web.Data;
using GymTracker.Web.Features.Admin;
using GymTracker.Web.Features.Feedback;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Users;

/// <summary>
/// Redeeming a single-use invite on the join page (SPEC 9). The developer makes them with <see cref="Admin.InviteAdmin"/>.
/// </summary>
public class InviteService(AppDbContext db, UserCreator creator, AdminAuditLog audit, IFeedbackNotifier notifier, TimeProvider time)
{
    public Task<bool> IsOpenAsync(string token, CancellationToken ct)
    {
        var hash = InviteRules.Hash(token);
        return db.Invites.WhereOpen(time.GetUtcNow()).AnyAsync(i => i.TokenHash == hash, ct);
    }

    /// <summary>
    /// Marks the invite used and creates the account in one transaction. Under concurrent redemptions the second
    /// update waits on the first one's row lock and then matches nothing, so one token makes one account. If the
    /// account can't be created the transaction rolls back and the invite stays open.
    /// </summary>
    public async Task<Redemption> RedeemAsync(string token, string name, string email, string password, Language language, CancellationToken ct)
    {
        var hash = InviteRules.Hash(token);
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.Invites.WhereOpen(now).Where(i => i.TokenHash == hash)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.RedeemedAt, now), ct);
        if (claimed == 0)
        {
            return Redemption.Invalid;
        }

        var created = await creator.CreateUserAsync(email, name, password, language, ct);
        if (created.User is not { } user)
        {
            await transaction.RollbackAsync(ct);
            return new Redemption(RedemptionStatus.Failed, null, created.Errors);
        }

        var invite = await db.Invites.SingleAsync(i => i.TokenHash == hash, ct);
        invite.RedeemedByUserId = user.Id;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        await audit.WriteAsync(AuditActions.InviteRedeem, user.Email, invite.Note, ct);
        notifier.EnqueueText($"Repa: {name.Trim()} joined with the invite \"{invite.Note ?? "(no note)"}\".");
        return new Redemption(RedemptionStatus.Redeemed, user, []);
    }

}

public enum RedemptionStatus
{
    Redeemed,

    /// <summary>The token is unknown, used, revoked or expired. The page says the same for all four.</summary>
    Invalid,

    /// <summary>The account couldn't be created (email taken, password too short); the invite stays open.</summary>
    Failed,
}

public sealed record Redemption(RedemptionStatus Status, AppUser? User, IReadOnlyList<string> Errors)
{
    public static readonly Redemption Invalid = new(RedemptionStatus.Invalid, null, []);
}
