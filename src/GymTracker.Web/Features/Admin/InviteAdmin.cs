using GymTracker.Core.Domain;
using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Admin;

/// <summary>
/// Making, listing and revoking invites, for the CLI and the admin area (SPEC 9 and 12). Redeeming one is
/// <see cref="Users.InviteService"/>. Every change is audited.
/// </summary>
public class InviteAdmin(AppDbContext db, AdminAuditLog audit, TimeProvider time)
{
    /// <summary>Makes an invite and returns it with its token, which is shown once and never stored.</summary>
    public async Task<(Invite Invite, string Token)> CreateAsync(string? createdByUserId, string? note, int days, CancellationToken ct)
    {
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note is { Length: > InviteRules.MaxNoteLength })
        {
            throw new ValidationException($"The note can be at most {InviteRules.MaxNoteLength} characters.");
        }

        if (!InviteRules.ValidDays(days))
        {
            throw new ValidationException($"An invite lasts {InviteRules.MinDays} to {InviteRules.MaxDays} days.");
        }

        var now = time.GetUtcNow();
        if (await db.Invites.WhereOpen(now).CountAsync(ct) >= InviteRules.MaxOpen)
        {
            throw new ValidationException($"There are already {InviteRules.MaxOpen} open invites. Revoke one or wait for it to expire.");
        }

        var (token, hash) = InviteRules.NewToken();
        var invite = new Invite { TokenHash = hash, Note = note, CreatedByUserId = createdByUserId, CreatedAt = now, ExpiresAt = now.AddDays(days) };
        db.Invites.Add(invite);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(AuditActions.InviteCreate, invite.Id.ToString(), note, ct);
        return (invite, token);
    }

    /// <summary>Open invites, or every invite with <paramref name="all"/>; newest first.</summary>
    public async Task<List<InviteRow>> ListAsync(bool all, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var invites = await (all ? db.Invites : db.Invites.WhereOpen(now)).AsNoTracking().OrderByDescending(i => i.CreatedAt).ToListAsync(ct);
        var ids = invites.Select(i => i.RedeemedByUserId).OfType<string>().ToList();
        var emails = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Email, ct);
        return invites.Select(i => new InviteRow(i, InviteRules.Status(i, now), i.RedeemedByUserId is { } id ? emails.GetValueOrDefault(id) : null)).ToList();
    }

    public async Task RevokeAsync(int id, CancellationToken ct)
    {
        var invite = await db.Invites.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException();
        if (InviteRules.Status(invite, time.GetUtcNow()) != InviteStatus.Open)
        {
            throw new ValidationException("Only an open invite can be revoked.");
        }

        invite.RevokedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(AuditActions.InviteRevoke, invite.Id.ToString(), invite.Note, ct);
    }
}

public sealed record InviteRow(Invite Invite, InviteStatus Status, string? RedeemedByEmail);
