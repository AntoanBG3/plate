using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Plate.Web.Features.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Plate.Web.Tests;

/// <summary>The app's own check of the Cloudflare Access token in front of the admin area (SPEC 12).</summary>
public class CloudflareAccessTests
{
    private const string Team = "example-team.cloudflareaccess.com";
    private const string Aud = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Email = "dev@example.com";
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Serves the team's certs: the current key set, which a test can swap to simulate Cloudflare rotating keys.</summary>
    private sealed class Certs : HttpMessageHandler, IHttpClientFactory
    {
        public string Json { get; set; } = "{\"keys\":[]}";
        public int Fetches { get; private set; }
        public Uri? Last { get; private set; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Fetches++;
            Last = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json) });
        }
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Plate";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static readonly RsaSecurityKey KeyA = NewKey("a");
    private static readonly RsaSecurityKey KeyB = NewKey("b");

    private static RsaSecurityKey NewKey(string id) => new(RSA.Create(2048)) { KeyId = id };

    private static string Jwks(params RsaSecurityKey[] keys) => JsonSerializer.Serialize(new
    {
        keys = keys.Select(k =>
        {
            var p = k.Rsa.ExportParameters(false);
            return new { kty = "RSA", kid = k.KeyId, alg = "RS256", use = "sig", n = WebEncoders.Base64UrlEncode(p.Modulus!), e = WebEncoders.Base64UrlEncode(p.Exponent!) };
        }),
    });

    private static string Token(SecurityKey key, string? aud = Aud, string issuer = "https://" + Team, string email = Email, DateTimeOffset? expires = null, string alg = SecurityAlgorithms.RsaSha256) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = aud,
            Subject = new ClaimsIdentity([new Claim("email", email)]),
            IssuedAt = Now.AddMinutes(-1).UtcDateTime,
            NotBefore = Now.AddMinutes(-1).UtcDateTime,
            Expires = (expires ?? Now.AddHours(1)).UtcDateTime,
            SigningCredentials = new SigningCredentials(key, alg),
        });

    private static (CloudflareAccess Access, Certs Certs, Clock Clock) Create(string environment = "Production", bool configured = true)
    {
        var certs = new Certs { Json = Jwks(KeyA) };
        var clock = new Clock(Now);
        var options = Options.Create(new CloudflareAccessOptions { TeamDomain = configured ? Team : null, Audience = configured ? Aud : null });
        return (new CloudflareAccess(certs, options, new Env(environment), clock, NullLogger<CloudflareAccess>.Instance), certs, clock);
    }

    private static HttpRequest Request(string? token)
    {
        var http = new DefaultHttpContext();
        if (token is not null)
        {
            http.Request.Headers[CloudflareAccess.HeaderName] = token;
        }

        return http.Request;
    }

    [Fact]
    public async Task A_token_signed_by_the_team_for_this_application_and_account_passes()
    {
        var (access, certs, _) = Create();
        (await access.VerifyAsync(Request(Token(KeyA)), Email, default)).Should().BeTrue();
        (await access.VerifyAsync(Request(Token(KeyA)), "DEV@example.com", default)).Should().BeTrue("emails compare without case");
        certs.Last!.AbsoluteUri.Should().Be($"https://{Team}/cdn-cgi/access/certs");
        (await access.VerifyAsync(Request(Token(KeyA)), Email, default)).Should().BeTrue();
        certs.Fetches.Should().Be(1, "the keys are cached");
    }

    [Fact]
    public async Task Wrong_audience_issuer_account_expiry_or_signature_fails()
    {
        var (access, _, _) = Create();
        (await access.VerifyAsync(Request(null), Email, default)).Should().BeFalse();
        (await access.VerifyAsync(Request("not a token"), Email, default)).Should().BeFalse();
        (await access.VerifyAsync(Request(Token(KeyA, aud: "another-app")), Email, default)).Should().BeFalse();
        (await access.VerifyAsync(Request(Token(KeyA, issuer: "https://evil.cloudflareaccess.com")), Email, default)).Should().BeFalse();
        (await access.VerifyAsync(Request(Token(KeyA, email: "someone@example.com")), Email, default)).Should().BeFalse();
        (await access.VerifyAsync(Request(Token(KeyA, expires: Now.AddMinutes(-2))), Email, default)).Should().BeFalse();
        (await access.VerifyAsync(Request(Token(NewKey("a"))), Email, default)).Should().BeFalse("same key id, different key");
    }

    [Fact]
    public async Task Unsigned_and_symmetric_tokens_are_refused()
    {
        var (access, _, _) = Create();
        var header = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"JWT\"}"));
        var payload = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            iss = "https://" + Team, aud = Aud, email = Email, exp = Now.AddHours(1).ToUnixTimeSeconds(), nbf = Now.AddMinutes(-1).ToUnixTimeSeconds(),
        })));
        (await access.VerifyAsync(Request($"{header}.{payload}."), Email, default)).Should().BeFalse();

        var hmac = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = "a" };
        (await access.VerifyAsync(Request(Token(hmac, alg: SecurityAlgorithms.HmacSha256)), Email, default)).Should().BeFalse();
    }

    [Fact]
    public async Task A_new_key_id_refetches_the_keys_but_not_more_than_once_a_minute()
    {
        var (access, certs, clock) = Create();
        (await access.VerifyAsync(Request(Token(KeyA)), Email, default)).Should().BeTrue();

        // Cloudflare rotated: tokens are now signed with key b.
        certs.Json = Jwks(KeyB);
        clock.Now = Now.AddSeconds(30);
        (await access.VerifyAsync(Request(Token(KeyB)), Email, default)).Should().BeFalse("the keys were fetched under a minute ago");
        certs.Fetches.Should().Be(1);

        clock.Now = Now.AddMinutes(2);
        (await access.VerifyAsync(Request(Token(KeyB)), Email, default)).Should().BeTrue();
        certs.Fetches.Should().Be(2);
    }

    [Fact]
    public void Without_configuration_production_turns_the_admin_area_off_and_testing_skips_the_check()
    {
        Create("Production", configured: false).Access.Available.Should().BeFalse();
        Create("Testing", configured: false).Access.Available.Should().BeTrue();
        Create("Development", configured: false).Access.Available.Should().BeTrue();
        Create("Production").Access.Available.Should().BeTrue();
    }

    [Fact]
    public async Task Unconfigured_production_refuses_every_request()
    {
        var (access, _, _) = Create("Production", configured: false);
        (await access.VerifyAsync(Request(Token(KeyA)), Email, default)).Should().BeFalse();
        (await Create("Testing", configured: false).Access.VerifyAsync(Request(null), Email, default)).Should().BeTrue();
    }
}

/// <summary>The Access check wired into the admin policy, end to end.</summary>
public class CloudflareAccessAdminTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task With_Access_configured_the_admin_area_needs_a_valid_token_for_the_account()
    {
        var key = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "k1" };
        var p = key.Rsa.ExportParameters(false);
        var jwks = JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", kid = "k1", alg = "RS256", n = WebEncoders.Base64UrlEncode(p.Modulus!), e = WebEncoders.Base64UrlEncode(p.Exponent!) } } });
        using var app = new AppFactory(fixture,
            settings: new Dictionary<string, string?> { ["Admin:CloudflareAccess:TeamDomain"] = "team.cloudflareaccess.com", ["Admin:CloudflareAccess:Audience"] = "aud1" },
            services: s => s.AddHttpClient(CloudflareAccess.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new StaticHandler(jwks)));
        var (client, email, passkey) = await AdminTests.DevAsync(app);

        string Token(string forEmail) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://team.cloudflareaccess.com",
            Audience = "aud1",
            Subject = new ClaimsIdentity([new Claim("email", forEmail)]),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });

        (await client.GetAsync("/admin/unlock")).StatusCode.Should().Be(HttpStatusCode.NotFound, "no Access token");
        client.DefaultRequestHeaders.Add(CloudflareAccess.HeaderName, Token("someone.else@example.com"));
        (await client.GetAsync("/admin/unlock")).StatusCode.Should().Be(HttpStatusCode.NotFound, "the token is for another address");

        client.DefaultRequestHeaders.Remove(CloudflareAccess.HeaderName);
        client.DefaultRequestHeaders.Add(CloudflareAccess.HeaderName, Token(email));
        (await client.GetAsync("/admin/unlock")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AdminTests.UnlockAsync(client, passkey)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/admin")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed class StaticHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
