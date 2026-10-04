using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace GymTracker.Web.Features.Admin;

public class CloudflareAccessOptions
{
    /// <summary>The Zero Trust team domain, e.g. <c>example.cloudflareaccess.com</c>.</summary>
    public string? TeamDomain { get; set; }

    /// <summary>The Access application's AUD tag.</summary>
    public string? Audience { get; set; }

    public bool Configured => !string.IsNullOrWhiteSpace(TeamDomain) && !string.IsNullOrWhiteSpace(Audience);

    /// <summary>"https://team.cloudflareaccess.com", whether the setting has the scheme or a trailing slash or not.</summary>
    public string Issuer => "https://" + TeamDomain!.Trim().Replace("https://", "").TrimEnd('/');
}

/// <summary>
/// Checks the token Cloudflare Access adds to every request it lets through to <c>/admin*</c> (SPEC 12), so the
/// admin area can't be reached around Access, for example by a path variant Access doesn't match. Fails closed in
/// Production when it isn't configured; Development and Testing skip it unless configured.
/// </summary>
public sealed class CloudflareAccess(IHttpClientFactory http, IOptions<CloudflareAccessOptions> options, IHostEnvironment env,
    TimeProvider time, ILogger<CloudflareAccess> log)
{
    public const string HeaderName = "Cf-Access-Jwt-Assertion";
    public const string HttpClientName = "cloudflare-access";
    private static readonly TimeSpan KeysLifetime = TimeSpan.FromHours(1);
    // A token naming an unknown key refetches the keys at most this often, so forged key ids can't make us hammer Cloudflare.
    private static readonly TimeSpan RefetchGap = TimeSpan.FromMinutes(1);

    private readonly JsonWebTokenHandler handler = new() { MapInboundClaims = false };
    private readonly SemaphoreSlim fetching = new(1, 1);
    private IList<SecurityKey>? keys;
    private DateTimeOffset fetchedAt;

    /// <summary>False when Access is required but not configured: the admin area is then off.</summary>
    public bool Available => options.Value.Configured || !Required;

    private bool Required => !(env.IsDevelopment() || env.IsEnvironment("Testing"));

    public async Task<bool> VerifyAsync(HttpRequest request, string? accountEmail, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Configured)
        {
            return !Required;
        }

        var token = request.Headers[HeaderName].ToString();
        if (token.Length == 0 || string.IsNullOrEmpty(accountEmail))
        {
            log.LogWarning("Admin request without a Cloudflare Access token from {Ip}.", request.HttpContext.Connection.RemoteIpAddress);
            return false;
        }

        var result = await ValidateAsync(token, o, refetch: false, ct);
        if (result.Exception is SecurityTokenSignatureKeyNotFoundException && time.GetUtcNow() - fetchedAt > RefetchGap)
        {
            // Cloudflare rotates its signing keys; a token signed with a new one is the usual reason for an unknown key id.
            result = await ValidateAsync(token, o, refetch: true, ct);
        }

        if (!result.IsValid)
        {
            log.LogWarning("Cloudflare Access token refused from {Ip}: {Reason}", request.HttpContext.Connection.RemoteIpAddress, result.Exception?.GetType().Name);
            return false;
        }

        var email = result.Claims.TryGetValue("email", out var value) ? value as string : null;
        if (!string.Equals(email, accountEmail, StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("Cloudflare Access email does not match the signed-in account, from {Ip}.", request.HttpContext.Connection.RemoteIpAddress);
            return false;
        }

        return true;
    }

    private async Task<TokenValidationResult> ValidateAsync(string token, CloudflareAccessOptions o, bool refetch, CancellationToken ct)
    {
        IList<SecurityKey> signingKeys;
        try
        {
            signingKeys = await KeysAsync(o, refetch, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or ArgumentException)
        {
            log.LogWarning(ex, "Cloudflare Access keys could not be loaded.");
            return new TokenValidationResult { IsValid = false, Exception = ex };
        }

        return await handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = o.Issuer,
            ValidAudience = o.Audience,
            IssuerSigningKeys = signingKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            TryAllIssuerSigningKeys = false,
            LifetimeValidator = (notBefore, expires, _, p) =>
            {
                var now = time.GetUtcNow().UtcDateTime;
                return expires is { } e && e.Add(p.ClockSkew) >= now && (notBefore is not { } nb || nb.Subtract(p.ClockSkew) <= now);
            },
        });
    }

    private async Task<IList<SecurityKey>> KeysAsync(CloudflareAccessOptions o, bool refetch, CancellationToken ct)
    {
        if (!refetch && keys is { } cached && time.GetUtcNow() - fetchedAt < KeysLifetime)
        {
            return cached;
        }

        await fetching.WaitAsync(ct);
        try
        {
            if (!refetch && keys is { } again && time.GetUtcNow() - fetchedAt < KeysLifetime)
            {
                return again;
            }

            using var client = http.CreateClient(HttpClientName);
            var json = await client.GetStringAsync($"{o.Issuer}/cdn-cgi/access/certs", ct);
            keys = new JsonWebKeySet(json).GetSigningKeys();
            fetchedAt = time.GetUtcNow();
            return keys;
        }
        finally
        {
            fetching.Release();
        }
    }
}
