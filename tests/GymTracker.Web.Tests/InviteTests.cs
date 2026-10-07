using FluentAssertions;
using GymTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

/// <summary>Invites (SPEC 9, Phase 12).</summary>
[Collection(PostgresCollection.Name)]
public class InviteTests(PostgresFixture fixture)
{
    private static Invite NewInvite(byte[] hash) => new()
    {
        TokenHash = hash,
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(InviteRules.DefaultDays),
    };

    [Fact]
    public async Task Two_invites_cannot_share_a_token_hash()
    {
        var (_, hash) = InviteRules.NewToken();
        await using var db = fixture.CreateContext(null);
        db.Invites.Add(NewInvite(hash));
        await db.SaveChangesAsync();

        db.Invites.Add(NewInvite(hash));
        await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Deleting_the_account_an_invite_created_keeps_the_invite_and_clears_the_link()
    {
        var userId = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext(null);
        var invite = NewInvite(InviteRules.NewToken().Hash);
        invite.RedeemedAt = DateTimeOffset.UtcNow;
        invite.RedeemedByUserId = userId;
        db.Invites.Add(invite);
        await db.SaveChangesAsync();

        await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync();

        var stored = await db.Invites.AsNoTracking().SingleAsync(i => i.Id == invite.Id);
        stored.RedeemedByUserId.Should().BeNull();
        stored.RedeemedAt.Should().NotBeNull("the invite was still used");
    }
}
