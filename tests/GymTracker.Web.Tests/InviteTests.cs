using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Web.Data;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Admin;
using GymTracker.Web.Features.Feedback;
using GymTracker.Web.Features.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymTracker.Web.Tests;

/// <summary>Invites (SPEC 9, Phase 12).</summary>
public class InviteTests(PostgresFixture fixture) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private const string Password = "correct horse battery";
    private readonly AppFactory app = new(fixture);

    // The open-invite cap is global, so each test starts without invites.
    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateContext(null);
        await db.Invites.ExecuteDeleteAsync();
    }

    public Task DisposeAsync()
    {
        app.Dispose();
        return Task.CompletedTask;
    }

    private static Invite NewInvite(byte[] hash) => new()
    {
        TokenHash = hash,
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(InviteRules.DefaultDays),
    };

    private static string NewEmail() => $"{Guid.NewGuid():N}@test.local";

    private async Task<string> CreateAsync(string? note = "Ivan from the gym", int days = InviteRules.DefaultDays)
    {
        using var scope = app.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<InviteAdmin>().CreateAsync(null, note, days, default)).Token;
    }

    private async Task<Redemption> RedeemAsync(string token, string email, string password = Password, Language language = Language.English)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<InviteService>().RedeemAsync(token, "Ivan", email, password, language, default);
    }

    private async Task<bool> IsOpenAsync(string token)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<InviteService>().IsOpenAsync(token, default);
    }

    private async Task<Invite> StoredAsync(string token)
    {
        await using var db = fixture.CreateContext(null);
        var hash = InviteRules.Hash(token);
        return await db.Invites.AsNoTracking().SingleAsync(i => i.TokenHash == hash);
    }

    private async Task<bool> AccountExistsAsync(string email)
    {
        await using var db = fixture.CreateContext(null);
        return await db.Users.AnyAsync(u => u.Email == email);
    }

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

    [Fact]
    public async Task Creating_stores_only_the_hash_with_the_note_and_expiry_and_is_audited()
    {
        var before = DateTimeOffset.UtcNow;
        var token = await CreateAsync(" Ivan from the gym ", days: 3);

        var invite = await StoredAsync(token);
        invite.Note.Should().Be("Ivan from the gym");
        invite.ExpiresAt.Should().BeCloseTo(before.AddDays(3), TimeSpan.FromMinutes(1));
        invite.CreatedByUserId.Should().BeNull("made outside a web request, like the CLI");
        await using var db = fixture.CreateContext(null);
        (await db.AdminAudit.AnyAsync(a => a.Action == AuditActions.InviteCreate && a.Target == invite.Id.ToString())).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public async Task Expiry_outside_1_to_30_days_is_refused(int days) =>
        await this.Invoking(t => t.CreateAsync(days: days)).Should().ThrowAsync<ValidationException>();

    [Fact]
    public async Task A_note_over_100_characters_is_refused() =>
        await this.Invoking(t => t.CreateAsync(new string('x', 101))).Should().ThrowAsync<ValidationException>();

    [Fact]
    public async Task A_sixth_open_invite_is_refused_but_used_revoked_and_expired_ones_do_not_count()
    {
        var tokens = new List<string>();
        for (var i = 0; i < InviteRules.MaxOpen; i++)
        {
            tokens.Add(await CreateAsync());
        }

        await this.Invoking(t => t.CreateAsync()).Should().ThrowAsync<ValidationException>().WithMessage("*5 open invites*");

        (await RedeemAsync(tokens[0], NewEmail())).Status.Should().Be(RedemptionStatus.Redeemed);
        using (var scope = app.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<InviteAdmin>().RevokeAsync((await StoredAsync(tokens[1])).Id, default);
        }

        await using (var db = fixture.CreateContext(null))
        {
            var hash = InviteRules.Hash(tokens[2]);
            await db.Invites.Where(i => i.TokenHash == hash).ExecuteUpdateAsync(s => s.SetProperty(i => i.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        for (var i = 0; i < 3; i++)
        {
            await CreateAsync();
        }

        await this.Invoking(t => t.CreateAsync()).Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Only_an_open_invite_can_be_revoked_and_a_revoked_one_creates_nothing()
    {
        var token = await CreateAsync();
        var id = (await StoredAsync(token)).Id;
        using var scope = app.Services.CreateScope();
        var invites = scope.ServiceProvider.GetRequiredService<InviteAdmin>();

        await invites.RevokeAsync(id, default);
        await invites.Invoking(s => s.RevokeAsync(id, default)).Should().ThrowAsync<ValidationException>();
        await invites.Invoking(s => s.RevokeAsync(int.MaxValue, default)).Should().ThrowAsync<NotFoundException>();

        var email = NewEmail();
        (await RedeemAsync(token, email)).Should().Be(Redemption.Invalid);
        (await AccountExistsAsync(email)).Should().BeFalse();
    }

    [Fact]
    public async Task Redeeming_creates_the_account_in_the_chosen_language_and_marks_the_invite_used()
    {
        var token = await CreateAsync();
        (await IsOpenAsync(token)).Should().BeTrue();
        var email = NewEmail();

        var result = await RedeemAsync(token, email, language: Language.Bulgarian);

        result.Status.Should().Be(RedemptionStatus.Redeemed);
        result.User!.Email.Should().Be(email);
        await using var db = fixture.CreateContext(null);
        var profile = await db.Profiles.IgnoreQueryFilters().SingleAsync(p => p.UserId == result.User.Id);
        profile.Language.Should().Be(Language.Bulgarian);
        profile.DisplayName.Should().Be("Ivan");
        profile.OnboardedAt.Should().BeNull("a new account starts with the setup chat");

        var invite = await StoredAsync(token);
        invite.RedeemedAt.Should().NotBeNull();
        invite.RedeemedByUserId.Should().Be(result.User.Id);
        (await IsOpenAsync(token)).Should().BeFalse();
        (await db.AdminAudit.AnyAsync(a => a.Action == AuditActions.InviteRedeem && a.Target == email && a.Detail == "Ivan from the gym")).Should().BeTrue();
    }

    [Fact]
    public async Task A_used_expired_or_unknown_token_creates_nothing()
    {
        var used = await CreateAsync();
        (await RedeemAsync(used, NewEmail())).Status.Should().Be(RedemptionStatus.Redeemed);

        var expired = await CreateAsync();
        await using (var db = fixture.CreateContext(null))
        {
            var hash = InviteRules.Hash(expired);
            await db.Invites.Where(i => i.TokenHash == hash).ExecuteUpdateAsync(s => s.SetProperty(i => i.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        }

        foreach (var token in new[] { used, expired, InviteRules.NewToken().Token, "" })
        {
            var email = NewEmail();
            (await RedeemAsync(token, email)).Should().Be(Redemption.Invalid);
            (await AccountExistsAsync(email)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task Two_redemptions_of_one_token_at_once_create_one_account()
    {
        var token = await CreateAsync();
        var emails = new[] { NewEmail(), NewEmail() };

        var results = await Task.WhenAll(emails.Select(e => RedeemAsync(token, e)));

        results.Select(r => r.Status).Should().BeEquivalentTo([RedemptionStatus.Redeemed, RedemptionStatus.Invalid]);
        (await AccountExistsAsync(emails[0]) ^ await AccountExistsAsync(emails[1])).Should().BeTrue();
    }

    [Fact]
    public async Task A_taken_email_or_a_short_password_leaves_the_invite_open_and_creates_nothing()
    {
        var token = await CreateAsync();
        var (taken, _) = await app.CreateUserAsync();

        var duplicate = await RedeemAsync(token, taken);
        duplicate.Status.Should().Be(RedemptionStatus.Failed);
        duplicate.Errors.Should().NotBeEmpty();

        var email = NewEmail();
        var shortPassword = await RedeemAsync(token, email, password: "11 characte");
        shortPassword.Status.Should().Be(RedemptionStatus.Failed);
        shortPassword.Errors.Should().ContainMatch("*12*");
        (await AccountExistsAsync(email)).Should().BeFalse();

        (await IsOpenAsync(token)).Should().BeTrue();
        (await StoredAsync(token)).RedeemedAt.Should().BeNull();
        (await RedeemAsync(token, email)).Status.Should().Be(RedemptionStatus.Redeemed);
    }

    [Fact]
    public async Task A_redemption_sends_a_Telegram_message_with_the_note_and_the_name()
    {
        var telegram = new Telegram();
        using var alerting = new AppFactory(fixture,
            settings: new Dictionary<string, string?> { ["Telegram:BotToken"] = "123:secret", ["Telegram:ChatId"] = "42", ["Telegram:BaseUrl"] = "http://telegram.test/" },
            services: s => s.AddHttpClient(FeedbackNotifierWorker.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => telegram));
        _ = alerting.Server; // starts the host, and with it the notifier worker

        using var scope = alerting.Services.CreateScope();
        var (_, token) = await scope.ServiceProvider.GetRequiredService<InviteAdmin>().CreateAsync(null, "Ivan from the gym", 7, default);
        (await scope.ServiceProvider.GetRequiredService<InviteService>().RedeemAsync(token, "Ivan", NewEmail(), Password, Language.English, default)).Status.Should().Be(RedemptionStatus.Redeemed);

        (await telegram.NextAsync()).Should().Contain("Ivan joined").And.Contain("Ivan from the gym");
    }

    private sealed class Telegram : HttpMessageHandler
    {
        private readonly System.Threading.Channels.Channel<string> texts = System.Threading.Channels.Channel.CreateUnbounded<string>();

        public async Task<string> NextAsync() => await texts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            await texts.Writer.WriteAsync(body["text"]!.GetValue<string>(), ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
        }
    }
}
