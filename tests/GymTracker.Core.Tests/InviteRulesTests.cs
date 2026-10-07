using FluentAssertions;
using GymTracker.Core.Domain;

namespace GymTracker.Core.Tests;

public class InviteRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static Invite Open() => new() { CreatedAt = Now.AddDays(-1), ExpiresAt = Now.AddDays(6) };

    [Fact]
    public void An_unused_invite_before_its_expiry_is_open() =>
        InviteRules.Status(Open(), Now).Should().Be(InviteStatus.Open);

    [Fact]
    public void An_invite_expires_at_its_expiry_time_not_after_it()
    {
        var invite = Open();
        invite.ExpiresAt = Now;
        InviteRules.Status(invite, Now).Should().Be(InviteStatus.Expired);
        InviteRules.Status(invite, Now.AddTicks(-1)).Should().Be(InviteStatus.Open);
    }

    [Fact]
    public void Redeemed_wins_over_revoked_and_expired()
    {
        var invite = Open();
        invite.RedeemedAt = Now.AddDays(-1);
        invite.RevokedAt = Now;
        invite.ExpiresAt = Now.AddDays(-1);
        InviteRules.Status(invite, Now).Should().Be(InviteStatus.Redeemed);
    }

    [Fact]
    public void Revoked_wins_over_expired()
    {
        var invite = Open();
        invite.RevokedAt = Now.AddDays(-2);
        invite.ExpiresAt = Now.AddDays(-1);
        InviteRules.Status(invite, Now).Should().Be(InviteStatus.Revoked);
    }

    [Fact]
    public void The_open_query_agrees_with_the_status()
    {
        var invites = new List<Invite>
        {
            Open(),
            new() { ExpiresAt = Now },
            new() { ExpiresAt = Now.AddDays(1), RedeemedAt = Now },
            new() { ExpiresAt = Now.AddDays(1), RevokedAt = Now },
        };

        invites.AsQueryable().WhereOpen(Now).Should().Equal(invites.Where(i => InviteRules.Status(i, Now) == InviteStatus.Open));
    }

    [Fact]
    public void A_token_is_43_url_safe_characters_and_comes_with_its_hash()
    {
        var (token, hash) = InviteRules.NewToken();
        token.Should().HaveLength(43).And.MatchRegex("^[A-Za-z0-9_-]+$");
        hash.Should().Equal(InviteRules.Hash(token));
    }

    [Fact]
    public void Two_tokens_differ() =>
        InviteRules.NewToken().Token.Should().NotBe(InviteRules.NewToken().Token);

    [Fact]
    public void The_hash_is_32_bytes_and_deterministic()
    {
        InviteRules.Hash("abc").Should().HaveCount(32).And.Equal(InviteRules.Hash("abc"));
        InviteRules.Hash("abc").Should().NotEqual(InviteRules.Hash("abd"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a token at all, with spaces and ünïcode")]
    public void Any_string_hashes_so_a_malformed_token_simply_matches_nothing(string token) =>
        InviteRules.Hash(token).Should().HaveCount(32);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(7, true)]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public void Expiry_is_1_to_30_days(int days, bool valid) =>
        InviteRules.ValidDays(days).Should().Be(valid);
}
