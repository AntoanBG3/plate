using GymTracker.Web.Data;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Admin;
using GymTracker.Web.Features.Feedback;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using GymTracker.Web.ViewModels;

namespace GymTracker.Web.Controllers;

/// <summary>
/// The admin area's passkey unlock (SPEC 12). Its own controller because its policy asks for the role and Cloudflare
/// Access but not for an unlock; policies on a controller and its actions add up, and [AllowAnonymous] would skip both.
/// </summary>
[Route("admin")]
[Authorize(Policy = AdminPolicy.Unlock)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class AdminUnlockController(
    UserManager<AppUser> users,
    SignInManager<AppUser> signIn,
    AdminAuditLog audit,
    IFeedbackNotifier notifier,
    TimeProvider time,
    ILogger<AdminUnlockController> log) : Controller
{
    [HttpGet("unlock")]
    public async Task<IActionResult> Unlock(string? returnUrl)
    {
        var user = await users.GetUserAsync(User);
        var hasPasskey = user is not null && (await users.GetPasskeysAsync(user)).Count > 0;
        return View(new AdminUnlockViewModel(SafeAdminUrl(returnUrl), hasPasskey));
    }

    [HttpPost("unlock/options")]
    [EnableRateLimiting(RateLimits.AdminUnlock)]
    public async Task<IActionResult> UnlockOptions()
    {
        var user = await users.GetUserAsync(User) ?? throw new NotFoundException();
        // Scoped to this account's own passkeys, so another account's passkey can't even be offered.
        return Content(await signIn.MakePasskeyRequestOptionsAsync(user), "application/json");
    }

    [HttpPost("unlock")]
    [EnableRateLimiting(RateLimits.AdminUnlock)]
    public async Task<IActionResult> UnlockWithPasskey([FromBody] Controllers.PasskeyCredential body, CancellationToken ct)
    {
        var user = await users.GetUserAsync(User) ?? throw new NotFoundException();
        PasskeyAssertionResult<AppUser> result;
        try
        {
            result = await signIn.PerformPasskeyAssertionAsync(body.Credential ?? "");
        }
        catch (Exception e) when (e is PasskeyException or System.Text.Json.JsonException or FormatException)
        {
            result = PasskeyAssertionResult.Fail<AppUser>(new PasskeyException("The credential could not be read."));
        }

        // The passkey must belong to the signed-in account and have checked the person (Face ID, Touch ID or PIN).
        if (!result.Succeeded || result.User is null || result.User.Id != user.Id || !result.Passkey.IsUserVerified)
        {
            var reason = result.Succeeded ? "another account's passkey, or no user verification" : result.Failure?.Message;
            log.LogWarning("Failed admin unlock for {User} from {Ip}: {Reason}", user.UserName, HttpContext.Connection.RemoteIpAddress, reason);
            await audit.WriteAsync(AuditActions.UnlockFailed, null, reason, ct);
            return BadRequest();
        }

        // Keeps the passkey's signature counter current, as a sign-in does.
        await users.AddOrUpdatePasskeyAsync(user, result.Passkey);
        await AdminUnlock.SignInAsync(HttpContext, user.Id, await users.GetSecurityStampAsync(user), time.GetUtcNow());
        await audit.WriteAsync(AuditActions.Unlock, null, DeviceName(), ct);
        notifier.EnqueueText($"GymTracker admin unlocked by {user.Email} on {DeviceName()} from {HttpContext.Connection.RemoteIpAddress}. If this wasn't you, run: role revoke dev --email {user.Email}");
        return Json(new { redirect = SafeAdminUrl(body.ReturnUrl) });
    }

    private string SafeAdminUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) && returnUrl.StartsWith("/admin", StringComparison.Ordinal) ? returnUrl : "/admin";

    private string DeviceName()
    {
        var ua = Request.Headers.UserAgent.ToString();
        return ua.Contains("iPhone") ? "iPhone" : ua.Contains("iPad") ? "iPad" : ua.Contains("Macintosh") ? "Mac" : ua.Contains("Android") ? "Android" : ua.Contains("Windows") ? "Windows" : "an unknown device";
    }
}
