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
        PasskeyAssertionResult<AppUser> assertion;
        try
        {
            assertion = await signIn.PerformPasskeyAssertionAsync(body.Credential ?? "");
        }
        catch (Exception e) when (e is PasskeyException or System.Text.Json.JsonException or FormatException)
        {
            return Reject("unreadable assertion");
        }

        if (!assertion.Succeeded)
        {
            return Reject(assertion.Failure.Message);
        }

        // SignInManager.PasskeySignInAsync would do these steps too, but always with a session cookie. The app is an
        // installed PWA and must stay signed in like after a password sign-in, so sign in persistently here.
        var user = assertion.User;
        if (!await signIn.CanSignInAsync(user) || await users.IsLockedOutAsync(user))
        {
            return Reject("locked out or not allowed");
        }

        // Keeps the authenticator's signature counter current, which is how a cloned authenticator would show.
        if (!(await users.AddOrUpdatePasskeyAsync(user, assertion.Passkey)).Succeeded)
        {
            return Reject("could not update the passkey");
        }

        await signIn.SignInAsync(user, isPersistent: true, authenticationMethod: "passkey");
        var returnUrl = body.ReturnUrl;
        return Json(new { redirect = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/today" });
    }

    private BadRequestResult Reject(string reason)
    {
        logger.LogWarning("Failed passkey sign-in from {Ip}: {Reason}", HttpContext.Connection.RemoteIpAddress, reason);
        return BadRequest();
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
        return Redirect("/settings");
    }

    /// <summary>A short label so the list in Settings tells an iPhone from a Mac.</summary>
    private static string DeviceName(string userAgent) =>
        userAgent.Contains("iPhone") ? "iPhone"
        : userAgent.Contains("iPad") ? "iPad"
        : userAgent.Contains("Macintosh") ? "Mac"
        : userAgent.Contains("Android") ? "Android"
        : userAgent.Contains("Windows") ? "Windows"
        : "Passkey";
}
