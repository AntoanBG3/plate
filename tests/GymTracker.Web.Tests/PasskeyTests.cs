using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FluentAssertions;
using GymTracker.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class PasskeyTests(PostgresFixture db) : IDisposable
{
    private readonly AppFactory app = new(db);

    public void Dispose() => app.Dispose();

    private async Task<(HttpClient Client, string Email)> SignedInAsync()
    {
        var (email, password) = await app.CreateUserAsync();
        var client = app.NewClient();
        await AppFactory.LoginAsync(client, email, password);
        return (client, email);
    }

    [Fact]
    public async Task Creation_options_need_a_signed_in_user()
    {
        var client = app.NewClient();
        var token = await AppFactory.TokenAsync(client, "/account/login");

        var response = await AppFactory.PostAsync(client, "/account/passkey/creation-options", token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Creation_options_carry_a_challenge_for_the_signed_in_user()
    {
        var (client, email) = await SignedInAsync();
        var token = await AppFactory.TokenAsync(client, "/settings");

        var response = await AppFactory.PostAsync(client, "/account/passkey/creation-options", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("\"challenge\"").And.Contain(email);
    }

    [Fact]
    public async Task Request_options_name_no_account()
    {
        // Usernameless sign-in: the options must not reveal which accounts exist.
        var client = app.NewClient();
        var token = await AppFactory.TokenAsync(client, "/account/login");

        var response = await AppFactory.PostAsync(client, "/account/passkey/request-options", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"challenge\"").And.NotContain("allowCredentials\":[{");
    }

    [Fact]
    public async Task A_garbage_assertion_does_not_sign_in()
    {
        var client = app.NewClient();
        var token = await AppFactory.TokenAsync(client, "/account/login");
        await AppFactory.PostAsync(client, "/account/passkey/request-options", token);
        var request = new HttpRequestMessage(HttpMethod.Post, "/account/passkey/login")
        {
            Content = JsonContent.Create(new { credential = "{\"id\":\"AAAA\",\"rawId\":\"AAAA\",\"type\":\"public-key\",\"response\":{}}" }),
        };
        request.Headers.Add("RequestVerificationToken", token);

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AppFactory.IsRedirectTo(await client.GetAsync("/today"), "https://localhost/account/login").Should().BeTrue();
    }

    [Fact]
    public async Task One_user_cannot_remove_another_users_passkey()
    {
        var (aEmail, _) = await app.CreateUserAsync();
        var credentialId = RandomNumberGenerator.GetBytes(16);
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var a = await users.FindByNameAsync(aEmail);
            var passkey = new UserPasskeyInfo(credentialId, RandomNumberGenerator.GetBytes(77), DateTimeOffset.UtcNow, 0, ["internal"],
                true, true, true, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
            (await users.AddOrUpdatePasskeyAsync(a!, passkey)).Succeeded.Should().BeTrue();
        }

        var (b, _) = await SignedInAsync();
        var token = await AppFactory.TokenAsync(b, "/settings");
        var response = await AppFactory.PostAsync(b, $"/settings/passkeys/{WebEncoders.Base64UrlEncode(credentialId)}/remove", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var a = await users.FindByNameAsync(aEmail);
            (await users.GetPasskeysAsync(a!)).Should().ContainSingle(p => p.CredentialId.SequenceEqual(credentialId));
        }
    }

    [Fact]
    public async Task Users_can_remove_their_own_passkey()
    {
        var (client, email) = await SignedInAsync();
        var credentialId = RandomNumberGenerator.GetBytes(16);
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = await users.FindByNameAsync(email);
            await users.AddOrUpdatePasskeyAsync(user!, new UserPasskeyInfo(credentialId, RandomNumberGenerator.GetBytes(77), DateTimeOffset.UtcNow, 0, ["internal"],
                true, true, true, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32)));
        }

        (await client.GetStringAsync("/settings")).Should().Contain(WebEncoders.Base64UrlEncode(credentialId));
        var token = await AppFactory.TokenAsync(client, "/settings");
        var response = await AppFactory.PostAsync(client, $"/settings/passkeys/{WebEncoders.Base64UrlEncode(credentialId)}/remove", token);

        AppFactory.IsRedirectTo(response, "/settings").Should().BeTrue();
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            (await users.GetPasskeysAsync((await users.FindByNameAsync(email))!)).Should().BeEmpty();
        }
    }
}
