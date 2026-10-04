using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace GymTracker.Web.Features.Admin;

/// <summary>
/// The passkey unlock for the admin area (SPEC 12): a second, short-lived cookie next to the sign-in cookie. It names
/// the account and its security stamp, so it stops working on log out everywhere or a password change, and it lasts
/// 30 minutes from the last admin request and 4 hours at most.
/// </summary>
public static class AdminUnlock
{
    public const string Scheme = "GymAdmin";
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan Max = TimeSpan.FromHours(4);
    private const string StampClaim = "stamp";
    private const string UnlockedAtClaim = "unlocked_at";

    public static void Configure(CookieAuthenticationOptions o, bool hostCookies)
    {
        o.Cookie.Name = hostCookies ? "__Host-gym.admin" : "gym.admin";
        o.Cookie.Path = "/";
        o.Cookie.HttpOnly = true;
        // Lax like the sign-in cookie, so coming back from Cloudflare Access's login keeps the unlock. Every admin
        // write is a POST with an anti-forgery token.
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = hostCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = Idle;
        o.SlidingExpiration = true;
    }

    public static Task SignInAsync(HttpContext http, string userId, string? stamp, DateTimeOffset now)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(StampClaim, stamp ?? ""),
            new Claim(UnlockedAtClaim, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
        ], Scheme);
        // A session cookie: closing the browser locks the admin area too.
        return http.SignInAsync(Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false });
    }

    public static Task LockAsync(HttpContext http) => http.SignOutAsync(Scheme);

    /// <summary>When the unlock ends, or null when there is no valid unlock for this account.</summary>
    public static async Task<DateTimeOffset?> ExpiresAsync(HttpContext http, string userId, string? stamp, DateTimeOffset now)
    {
        var result = await http.AuthenticateAsync(Scheme);
        return result.Succeeded ? Expires(result.Principal, result.Properties.ExpiresUtc, userId, stamp, now) : null;
    }

    /// <summary>The checks on an authenticated unlock cookie, apart from its idle expiry, which the cookie handler enforces.</summary>
    internal static DateTimeOffset? Expires(ClaimsPrincipal unlock, DateTimeOffset? idleExpiry, string userId, string? stamp, DateTimeOffset now)
    {
        if (unlock.FindFirstValue(ClaimTypes.NameIdentifier) != userId
            || unlock.FindFirstValue(StampClaim) != (stamp ?? "")
            || !long.TryParse(unlock.FindFirstValue(UnlockedAtClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        var hardLimit = DateTimeOffset.FromUnixTimeSeconds(seconds) + Max;
        if (now >= hardLimit)
        {
            return null;
        }

        return idleExpiry is { } idle && idle < hardLimit ? idle : hardLimit;
    }
}
