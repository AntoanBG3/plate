using System.Net;
using FluentAssertions;
using GymTracker.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace GymTracker.Web.Tests;

public class LoginHardeningTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    private sealed class CountingHasher : PasswordHasher<AppUser>
    {
        public int Verifies;

        public override PasswordVerificationResult VerifyHashedPassword(AppUser user, string hashedPassword, string providedPassword)
        {
            Interlocked.Increment(ref Verifies);
            return base.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    [Fact]
    public async Task Unknown_emails_cost_the_same_password_hash_as_known_ones()
    {
        var hasher = new CountingHasher();
        using var app = new AppFactory(db);
        using var withHasher = app.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IPasswordHasher<AppUser>>(hasher)));
        var client = withHasher.CreateClient(AppFactory.HttpsClientOptions);

        await AppFactory.LoginAsync(client, "nobody@test.local", "some long password");

        hasher.Verifies.Should().Be(1, "a missing account must still run one hash so timing doesn't reveal it");
    }

    [Fact]
    public async Task Login_attempts_are_limited_across_all_addresses()
    {
        using var app = new AppFactory(db, settings: new Dictionary<string, string?> { ["RateLimits:LoginGlobalPerWindow"] = "3" });
        var client = app.NewClient();

        for (var i = 0; i < 3; i++)
        {
            (await AppFactory.LoginAsync(client, "nobody@test.local", "whatever password")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await AppFactory.LoginAsync(client, "nobody@test.local", "whatever password")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Password_changes_are_rate_limited_per_user()
    {
        using var app = new AppFactory(db, settings: new Dictionary<string, string?> { ["RateLimits:SensitivePerWindow"] = "2" });
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/settings");
        var wrong = new Dictionary<string, string>
        {
            ["currentPassword"] = "not it at all", ["newPassword"] = "another long password", ["confirmPassword"] = "another long password",
        };

        await AppFactory.PostAsync(client, "/settings/password", token, wrong);
        await AppFactory.PostAsync(client, "/settings/password", token, wrong);

        (await AppFactory.PostAsync(client, "/settings/password", token, wrong)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }
}
