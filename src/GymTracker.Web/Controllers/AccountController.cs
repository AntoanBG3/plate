using GymTracker.Web.Data;
using GymTracker.Web.Features;
using GymTracker.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GymTracker.Web.Controllers;

[Route("account")]
public class AccountController(SignInManager<AppUser> signIn) : Controller
{
    [AllowAnonymous]
    [HttpGet("login")]
    public IActionResult Login(string? returnUrl) =>
        User.Identity?.IsAuthenticated == true ? LocalRedirect(SafeReturnUrl(returnUrl)) : View(new LoginViewModel(null, returnUrl, null));

    [AllowAnonymous]
    [HttpPost("login")]
    [EnableRateLimiting(RateLimits.Login)]
    public async Task<IActionResult> Login(string email, string password, string? returnUrl)
    {
        // Always persistent: the app is used as an installed PWA and should stay signed in.
        var result = await signIn.PasswordSignInAsync(email?.Trim() ?? "", password ?? "", isPersistent: true, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            return LocalRedirect(SafeReturnUrl(returnUrl));
        }

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
