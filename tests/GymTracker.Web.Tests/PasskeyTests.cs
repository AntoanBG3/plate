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

    /// <summary>Stores a made-up passkey on the account directly; the tests can't run a real WebAuthn ceremony.</summary>
    private async Task<byte[]> AddPasskeyAsync(string email)
    {
        var credentialId = RandomNumberGenerator.GetBytes(16);
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var passkey = new UserPasskeyInfo(credentialId, RandomNumberGenerator.GetBytes(77), DateTimeOffset.UtcNow, 0, ["internal"],
            true, true, true, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
        (await users.AddOrUpdatePasskeyAsync((await users.FindByNameAsync(email))!, passkey)).Succeeded.Should().BeTrue();
        return credentialId;
    }

    private async Task<IList<UserPasskeyInfo>> PasskeysAsync(string email)
    {
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        return await users.GetPasskeysAsync((await users.FindByNameAsync(email))!);
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
        var (client, email) = await app.SignedInClientAsync();
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
        var credentialId = await AddPasskeyAsync(aEmail);

        var (b, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(b, "/settings");
        var response = await AppFactory.PostAsync(b, $"/settings/passkeys/{WebEncoders.Base64UrlEncode(credentialId)}/remove", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await PasskeysAsync(aEmail)).Should().ContainSingle(p => p.CredentialId.SequenceEqual(credentialId));
    }

    [Fact]
    public async Task Users_can_remove_their_own_passkey()
    {
        var (client, email) = await app.SignedInClientAsync();
        var credentialId = await AddPasskeyAsync(email);

        (await client.GetStringAsync("/settings")).Should().Contain(WebEncoders.Base64UrlEncode(credentialId));
        var token = await AppFactory.TokenAsync(client, "/settings");
        var response = await AppFactory.PostAsync(client, $"/settings/passkeys/{WebEncoders.Base64UrlEncode(credentialId)}/remove", token);

        AppFactory.IsRedirectTo(response, "/settings").Should().BeTrue();
        (await PasskeysAsync(email)).Should().BeEmpty();
    }
}
