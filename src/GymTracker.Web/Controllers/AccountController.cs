using GymTracker.Web.Data;
using GymTracker.Web.Features;
using GymTracker.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GymTracker.Web.Controllers;

[Route("account")]
public class AccountController(
    SignInManager<AppUser> signIn, UserManager<AppUser> users, IPasswordHasher<AppUser> hasher, ILogger<AccountController> logger) : Controller
{
    // A real hash to check against when the email is unknown, so both paths cost one password hash.
    private static string? dummyHash;

    [AllowAnonymous]
    [HttpGet("login")]
    public IActionResult Login(string? returnUrl) =>
        User.Identity?.IsAuthenticated == true ? LocalRedirect(SafeReturnUrl(returnUrl)) : View(new LoginViewModel(null, returnUrl, null));

    [AllowAnonymous]
    [HttpPost("login")]
    [EnableRateLimiting(RateLimits.Login)]
    public async Task<IActionResult> Login(string email, string password, string? returnUrl)
    {
        email = email?.Trim() ?? "";
        password ??= "";
        var user = await users.FindByNameAsync(email);
        Microsoft.AspNetCore.Identity.SignInResult result;
        if (user is null)
        {
            var probe = new AppUser();
            dummyHash ??= hasher.HashPassword(probe, Guid.NewGuid().ToString());
            hasher.VerifyHashedPassword(probe, dummyHash, password);
            result = Microsoft.AspNetCore.Identity.SignInResult.Failed;
        }
        else
        {
            // Always persistent: the app is used as an installed PWA and should stay signed in.
            result = await signIn.PasswordSignInAsync(user, password, isPersistent: true, lockoutOnFailure: true);
        }

        if (result.Succeeded)
        {
            return LocalRedirect(SafeReturnUrl(returnUrl));
        }

        logger.LogWarning("Failed sign-in for {Email} from {Ip}: {Reason}", email, HttpContext.Connection.RemoteIpAddress,
            user is null ? "unknown email" : result.IsLockedOut ? "locked out" : "wrong password");

        var error = result.IsLockedOut
            ? "Too many failed attempts. Try again in 15 minutes."
            : "Email or password is wrong.";
        return View(new LoginViewModel(email, returnUrl, error));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        return Redirect("/account/login");
    }

    private string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/today";
}
