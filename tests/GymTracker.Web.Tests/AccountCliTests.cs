using System.Net;
using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Web.Cli;
using GymTracker.Web.Data;
using GymTracker.Web.Features.Admin;
using GymTracker.Web.Features.Body;
using GymTracker.Web.Features.Profile;
using GymTracker.Web.Features.Training;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymTracker.Web.Tests;

/// <summary>The invite, reset-password and delete-user commands (SPEC 12, Phase 12).</summary>
[Collection(PostgresCollection.Name)]
public class AccountCliTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly AppFactory app = new(fixture);

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

    private static async Task<(int Code, string Output)> RunAsync(Func<TextWriter, Task<int>> command)
    {
        var output = new StringWriter();
        var code = await command(output);
        return (code, output.ToString());
    }

    [Fact]
    public async Task Invite_create_prints_the_link_once_and_list_and_revoke_follow_it()
    {
        var (code, created) = await RunAsync(o => InviteCommand.RunAsync(app.Services, ["invite", "create", "--note", "Ivan from the gym", "--expires", "3d"], o));
        code.Should().Be(0);
        var link = created.Split('\n').Single(l => l.StartsWith("https://", StringComparison.Ordinal)).Trim();
        link.Should().StartWith("https://gym.peychev.uk/join/");
        var token = link["https://gym.peychev.uk/join/".Length..];
        token.Should().HaveLength(43);

        var (_, list) = await RunAsync(o => InviteCommand.RunAsync(app.Services, ["invite", "list"], o));
        list.Should().Contain("open").And.Contain("Ivan from the gym").And.NotContain(token);
        var id = list.Split("  ")[0];

        (await RunAsync(o => InviteCommand.RunAsync(app.Services, ["invite", "revoke", id], o))).Should().Be((0, $"Invite {id} revoked.{Environment.NewLine}"));
        (await RunAsync(o => InviteCommand.RunAsync(app.Services, ["invite", "list"], o))).Output.Should().Be($"0 invites.{Environment.NewLine}");
        (await RunAsync(o => InviteCommand.RunAsync(app.Services, ["invite", "list", "--all"], o))).Output.Should().Contain("revoked");
        (await RunAsync(o => InviteCommand.RunAsync(app.Services, ["invite", "revoke", id], o))).Code.Should().Be(1);

        var (_, audit) = await RunAsync(o => AdminCommand.RunAsync(app.Services, ["admin", "audit", "--since", "1h"], o));
        audit.Should().Contain($"cli  invite-create  {id}  Ivan from the gym").And.Contain($"cli  invite-revoke  {id}");
    }

    [Theory]
    [InlineData("7d", 7)]
    [InlineData("30", 30)]
    [InlineData(null, InviteRules.DefaultDays)]
    [InlineData("1w", null)]
    [InlineData("-3d", null)]
    public void Expiry_is_a_number_of_days(string? text, int? days) =>
        InviteCommand.Days(text).Should().Be(days);

    [Fact]
    public async Task Invite_create_refuses_an_expiry_out_of_range() =>
        (await RunAsync(o => InviteCommand.RunAsync(app.Services, ["invite", "create", "--expires", "31d"], o))).Code.Should().Be(1);

    [Fact]
    public async Task Reset_password_sets_the_new_one_clears_the_lockout_and_changes_the_stamp()
    {
        var (email, oldPassword) = await app.CreateUserAsync();
        string stampBefore;
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByEmailAsync(email))!;
            await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(15));
            stampBefore = user.SecurityStamp!;
        }

        (await RunAsync(o => ResetPasswordCommand.RunAsync(app.Services, ["reset-password", "--email", email], o, new StringReader("short\n")))).Code
            .Should().Be(1, "the 12-character minimum still applies");

        var (code, output) = await RunAsync(o => ResetPasswordCommand.RunAsync(app.Services, ["reset-password", "--email", email], o, new StringReader("a brand new passphrase\n")));
        code.Should().Be(0);
        output.Should().Contain($"Password reset for {email}");

        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByEmailAsync(email))!;
            user.SecurityStamp.Should().NotBe(stampBefore, "every other session must sign out");
            (await users.IsLockedOutAsync(user)).Should().BeFalse();
        }

        AppFactory.IsRedirectTo(await AppFactory.LoginAsync(app.NewClient(), email, oldPassword), "/").Should().BeFalse();
        AppFactory.IsRedirectTo(await AppFactory.LoginAsync(app.NewClient(), email, "a brand new passphrase"), "/").Should().BeTrue();

        (await RunAsync(o => ResetPasswordCommand.RunAsync(app.Services, ["reset-password", "--email", "nobody@test.local"], o, new StringReader("whatever long one\n")))).Code
            .Should().Be(1);
    }

    /// <summary>
    /// delete-user runs against its own database: "the last Dev" means nothing in the shared one, where other tests
    /// grant the role freely.
    /// </summary>
    [Fact]
    public async Task Delete_user_removes_every_row_the_account_owns_keeps_shared_ones_and_spares_the_last_dev()
    {
        var connection = await fixture.NewDatabaseAsync("delete_user");
        using var own = new AppFactory(fixture, settings: new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connection });
        var (doomed, _) = await own.CreateUserAsync();
        var (dev, _) = await own.CreateUserAsync();
        var (other, _) = await own.CreateUserAsync();
        await RoleCommand.RunAsync(own.Services, ["role", "grant", "dev", "--email", doomed], new StringWriter());

        string id, otherId;
        await using (var db = fixture.CreateContext(null, connection))
        {
            id = await db.Users.Where(u => u.Email == doomed).Select(u => u.Id).SingleAsync();
            otherId = await db.Users.Where(u => u.Email == other).Select(u => u.Id).SingleAsync();
        }

        var owned = await PopulateAsync(connection, id);
        await PopulateAsync(connection, otherId);
        int inviteId;
        await using (var db = fixture.CreateContext(null, connection))
        {
            var invite = new Invite { TokenHash = InviteRules.NewToken().Hash, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7), RedeemedAt = DateTimeOffset.UtcNow, RedeemedByUserId = id };
            db.Invites.Add(invite);
            await db.SaveChangesAsync();
            inviteId = invite.Id;
        }

        // The only Dev can't be deleted.
        var (refused, _) = await RunAsync(o => DeleteUserCommand.RunAsync(own.Services, ["delete-user", "--email", doomed, "--yes"], o));
        refused.Should().Be(1);
        await RoleCommand.RunAsync(own.Services, ["role", "grant", "dev", "--email", dev], new StringWriter());

        // A wrong confirmation deletes nothing.
        (await RunAsync(o => DeleteUserCommand.RunAsync(own.Services, ["delete-user", "--email", doomed], o, new StringReader("someone-else@test.local\n")))).Code.Should().Be(1);
        (await CountOwnedAsync(connection, id)).Should().NotBeEmpty();

        var (code, output) = await RunAsync(o => DeleteUserCommand.RunAsync(own.Services, ["delete-user", "--email", doomed], o, new StringReader(doomed.ToUpperInvariant() + "\n")));
        code.Should().Be(0, output);

        (await CountOwnedAsync(connection, id)).Should().BeEmpty("no table keeps a row of the deleted account");
        await using (var db = fixture.CreateContext(null, connection))
        {
            (await db.Sets.IgnoreQueryFilters().AnyAsync(s => owned.SetIds.Contains(s.Id))).Should().BeFalse();
            (await db.SessionExercises.IgnoreQueryFilters().AnyAsync(s => owned.SessionExerciseIds.Contains(s.Id))).Should().BeFalse();
            (await db.TemplateExercises.IgnoreQueryFilters().AnyAsync(t => owned.TemplateExerciseIds.Contains(t.Id))).Should().BeFalse();
            (await db.FeedbackAttachments.IgnoreQueryFilters().AnyAsync(a => a.FeedbackId == owned.FeedbackId)).Should().BeFalse();

            (await db.ErrorEvents.SingleAsync(e => e.Id == owned.ErrorEventId)).UserId.Should().BeNull("the error stays, without the account");
            var invite = await db.Invites.SingleAsync(i => i.Id == inviteId);
            invite.RedeemedByUserId.Should().BeNull();
            invite.RedeemedAt.Should().NotBeNull();
            (await db.FoodItems.IgnoreQueryFilters().SingleAsync(f => f.Id == owned.SharedFoodId)).AddedByUserId.Should().Be(id,
                "shared foods stay, still credited to an account that no longer exists, so others keep seeing it as another user's");
            (await db.LabelScanPhotos.IgnoreQueryFilters().AnyAsync(p => p.LabelScanId == owned.LabelScanId)).Should().BeFalse("label photos go with the account");
            (await db.AdminAudit.AnyAsync(a => a.Action == AuditActions.UserDelete && a.Target == id && a.Detail == doomed)).Should().BeTrue();
        }

        (await CountOwnedAsync(connection, otherId)).Should().NotBeEmpty("the other account is untouched");
        AppFactory.IsRedirectTo(await AppFactory.LoginAsync(own.NewClient(), other, "correct horse battery"), "/").Should().BeTrue();
    }

    private sealed record Owned(int[] SetIds, int[] SessionExerciseIds, int[] TemplateExerciseIds, int FeedbackId, int ErrorEventId, int SharedFoodId, int LabelScanId);

    /// <summary>
    /// One of everything an account can own, including a custom exercise used in a session and a template: the
    /// Restrict foreign keys from those to Exercise are what a plain cascade from the user row trips over.
    /// </summary>
    private async Task<Owned> PopulateAsync(string connection, string userId)
    {
        await using var db = fixture.CreateContext(userId, connection);
        var user = new FixedCurrentUser(userId);
        var templates = new TemplateService(db, user, TimeProvider.System);
        var sessions = new SessionService(db, user, new ProfileLookup(db), TimeProvider.System);

        var custom = new Exercise { Name = $"Secret move {userId[..4]}", PrimaryMuscle = Muscle.Chest, Equipment = Equipment.Other, OwnerUserId = userId };
        db.Exercises.Add(custom);
        await db.SaveChangesAsync();
        var template = await templates.CreateAsync("Push", default);
        await templates.AddExerciseAsync(template.Id, custom.Id, default);
        template = await templates.GetAsync(template.Id, default);
        var session = await sessions.GetAsync((await sessions.StartAsync(template.Id, default)).Id, default);
        var set = await sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), session.Exercises[0].Id, 100, 5, null, false, null), default);
        await new BodyweightService(db, user).SetAsync(new DateOnly(2026, 10, 1), 80, default);

        var privateFood = new FoodItem { Name = "Mum's banitsa", OwnerUserId = userId, KcalPer100g = 300 };
        var sharedFood = new FoodItem { Name = $"Scanned bar {userId[..4]}", Barcode = Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(), KcalPer100g = 400, AddedByUserId = userId, Source = FoodSource.Manual };
        var run = new ParseRun { UserId = userId, CreatedAt = DateTimeOffset.UtcNow, Text = "banitsa" };
        db.AddRange(privateFood, sharedFood, run);
        await db.SaveChangesAsync();
        db.FoodLog.Add(new FoodLogEntry { UserId = userId, Date = new DateOnly(2026, 10, 1), Description = "banitsa", Grams = 100, Kcal = 300, FoodItem = privateFood, ParseRunId = run.Id, CreatedAt = DateTimeOffset.UtcNow });
        var feedback = new Feedback
        {
            ClientId = Guid.NewGuid(), UserId = userId, CreatedAt = DateTimeOffset.UtcNow, Message = "report", ParseRunId = run.Id,
            Attachment = new FeedbackAttachment { ContentType = "image/png", Bytes = [1, 2, 3] },
        };
        var error = new ErrorEvent { Fingerprint = Guid.NewGuid().ToString("N")[..16], Type = "Boom", Message = "boom", UserId = userId, FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow, Count = 1 };
        var scan = new LabelScan
        {
            UserId = userId, CreatedAt = DateTimeOffset.UtcNow, Model = "test/label", FoodItemId = sharedFood.Id,
            Photos = [new LabelScanPhoto { ContentType = "image/jpeg", Bytes = [0xFF, 0xD8, 0xFF] }],
        };
        db.AddRange(feedback, error, new OnboardingChat { UserId = userId }, scan);
        await db.SaveChangesAsync();

        return new Owned([set.Id], [.. session.Exercises.Select(e => e.Id)], [.. template.Exercises.Select(e => e.Id)], feedback.Id, error.Id, sharedFood.Id, scan.Id);
    }

    /// <summary>"Table: count" for every table with a user column that still has rows for the account, except the audit log.</summary>
    private async Task<List<string>> CountOwnedAsync(string connection, string userId)
    {
        await using var db = fixture.CreateContext(null, connection);
        var found = new List<string>();
        foreach (var entity in db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is null || entity.ClrType == typeof(AdminAudit))
            {
                continue;
            }

            foreach (var property in entity.GetProperties().Where(p => p.Name is "UserId" or "OwnerUserId" or "RedeemedByUserId"))
            {
                var column = property.GetColumnName();
                // Table and column names come from the EF model, never from input; the user id is a parameter.
                var sql = $"SELECT count(*)::int AS \"Value\" FROM \"{table}\" WHERE \"{column}\" = {{0}}";
                var count = await db.Database.SqlQueryRaw<int>(sql, userId).SingleAsync();
                if (count > 0)
                {
                    found.Add($"{table}.{column}: {count}");
                }
            }
        }

        return found;
    }
}
