using GymTracker.Web.Data;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;

namespace GymTracker.Web.Controllers;

public record PasskeyCredential(string? Credential, string? ReturnUrl);

/// <summary>
/// Passkey (Face ID / Touch ID) registration and sign-in through ASP.NET Core Identity. The browser side is
/// wwwroot/js/passkey.js. Sign-in is usernameless, so the request options never name an account.
/// </summary>
public class PasskeyController(SignInManager<AppUser> signIn, UserManager<AppUser> users, ILogger<PasskeyController> logger) : Controller
{
    [HttpPost("account/passkey/creation-options")]
    [EnableRateLimiting(RateLimits.Sensitive)]
    public async Task<IActionResult> CreationOptions()
    {
        var user = await users.GetUserAsync(User) ?? throw new NotFoundException();
        var name = await users.GetUserNameAsync(user) ?? "Gym";
        var options = await signIn.MakePasskeyCreationOptionsAsync(new PasskeyUserEntity
        {
            Id = await users.GetUserIdAsync(user),
            Name = name,
            DisplayName = name,
        });
        return Content(options, "application/json");
    }

    [HttpPost("account/passkey/register")]
    [EnableRateLimiting(RateLimits.Sensitive)]
    public async Task<IActionResult> Register([FromBody] PasskeyCredential body)
    {
        var user = await users.GetUserAsync(User) ?? throw new NotFoundException();
        var attestation = await signIn.PerformPasskeyAttestationAsync(body.Credential ?? "");
        if (!attestation.Succeeded)
        {
            logger.LogWarning("Passkey registration failed for {User}: {Reason}", user.UserName, attestation.Failure.Message);
            return BadRequest();
        }

        attestation.Passkey.Name = DeviceName(Request.Headers.UserAgent.ToString());
        var saved = await users.AddOrUpdatePasskeyAsync(user, attestation.Passkey);
        if (!saved.Succeeded)
        {
            return BadRequest();
        }

        TempData.Notice("Passkey added.");
        return NoContent();
    }

    [AllowAnonymous]
    [HttpPost("account/passkey/request-options")]
    [EnableRateLimiting(RateLimits.PasskeyOptions)]
    public async Task<IActionResult> RequestOptions() =>
        Content(await signIn.MakePasskeyRequestOptionsAsync(null), "application/json");

    [AllowAnonymous]
    [HttpPost("account/passkey/login")]
    [EnableRateLimiting(RateLimits.Login)]
    public async Task<IActionResult> Login([FromBody] PasskeyCredential body)
    {
        Microsoft.AspNetCore.Identity.SignInResult result;
        try
        {
            // Persistent like a password sign-in: the cookie's OnSigningIn event makes every sign-in persistent.
            result = await signIn.PasskeySignInAsync(body.Credential ?? "");
        }
        catch (Exception e) when (e is PasskeyException or System.Text.Json.JsonException or FormatException)
        {
            result = Microsoft.AspNetCore.Identity.SignInResult.Failed;
        }

        if (!result.Succeeded)
        {
            logger.LogWarning("Failed passkey sign-in from {Ip}: {Reason}", HttpContext.Connection.RemoteIpAddress,
                result.IsLockedOut ? "locked out" : "assertion failed");
            return BadRequest();
        }

        return Json(new { redirect = Url.SafeReturnUrl(body.ReturnUrl) });
    }

    [HttpPost("settings/passkeys/{id}/remove")]
    public async Task<IActionResult> Remove(string id)
    {
        var user = await users.GetUserAsync(User) ?? throw new NotFoundException();
        byte[] credentialId;
        try
        {
            credentialId = WebEncoders.Base64UrlDecode(id);
        }
        catch (FormatException)
        {
            throw new NotFoundException();
        }

        // Only a passkey on the signed-in account can be removed.
        if (await users.GetPasskeyAsync(user, credentialId) is null)
        {
            throw new NotFoundException();
        }

        await users.RemovePasskeyAsync(user, credentialId);
        TempData.Notice("Passkey removed.");
        return Redirect("/settings/security");
    }

    /// <summary>A short label so the list in Settings tells an iPhone from a Mac. Null when unknown; Settings shows a translated "Passkey".</summary>
    private static string? DeviceName(string userAgent) =>
        userAgent.Contains("iPhone") ? "iPhone"
        : userAgent.Contains("iPad") ? "iPad"
        : userAgent.Contains("Macintosh") ? "Mac"
        : userAgent.Contains("Android") ? "Android"
        : userAgent.Contains("Windows") ? "Windows"
        : null;
}
