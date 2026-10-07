using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace GymTracker.Core.Domain;

/// <summary>
/// A single-use link to create an account (SPEC 9). Not user-owned: only the developer creates and sees invites.
/// Only the token's hash is stored, so a link can be shown once and never again.
/// </summary>
public class Invite
{
    public int Id { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public string? Note { get; set; }

    /// <summary>The account that made it in the admin area; null when made with the CLI.</summary>
    public string? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RedeemedAt { get; set; }

    /// <summary>The account created from it; null until redeemed, and again if that account is deleted.</summary>
    public string? RedeemedByUserId { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}

public enum InviteStatus
{
    Open,
    Redeemed,
    Revoked,
    Expired,
}

public static class InviteRules
{
    public const int MaxOpen = 5;
    public const int MinDays = 1;
    public const int MaxDays = 30;
    public const int DefaultDays = 7;
    public const int MaxNoteLength = 100;

    private const int TokenBytes = 32;

    /// <summary>Redeemed, then revoked, then expired: what happened to it matters more than the clock.</summary>
    public static InviteStatus Status(Invite invite, DateTimeOffset now) =>
        invite.RedeemedAt is not null ? InviteStatus.Redeemed
        : invite.RevokedAt is not null ? InviteStatus.Revoked
        // The same test as the redemption's SQL condition, ExpiresAt > now.
        : invite.ExpiresAt <= now ? InviteStatus.Expired
        : InviteStatus.Open;

    public static bool ValidDays(int days) => days is >= MinDays and <= MaxDays;

    /// <summary>A new link token (32 random bytes, base64url) and the hash to store for it.</summary>
    public static (string Token, byte[] Hash) NewToken()
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        return (token, Hash(token));
    }

    /// <summary>SHA-256 of the token as typed. Anything hashes; a malformed token just matches no invite.</summary>
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
