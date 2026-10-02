using GymTracker.Web.Data;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Profile;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace GymTracker.Web.Controllers;

[Route("settings")]
public class SettingsController(ProfileService profiles, UserManager<AppUser> users, SignInManager<AppUser> signIn) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Email"] = (await users.GetUserAsync(User))?.Email;
        return View(await profiles.GetAsync(ct));
    }

    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword(string currentPassword, string newPassword, string confirmPassword)
    {
        if (newPassword != confirmPassword)
        {
            throw new ValidationException("The new passwords do not match.");
        }

        var user = await users.GetUserAsync(User) ?? throw new NotFoundException();
        var result = await users.ChangePasswordAsync(user, currentPassword ?? "", newPassword ?? "");
        if (!result.Succeeded)
        {
            throw new ValidationException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }

        // Changing the password updates the security stamp; sign in again so this device stays logged in.
        await signIn.SignInAsync(user, isPersistent: true);
        TempData["Message"] = "Password changed. Other devices will be signed out.";
        return Redirect("/settings");
    }

    [HttpPost("logout-everywhere")]
    public async Task<IActionResult> LogoutEverywhere()
    {
        var user = await users.GetUserAsync(User) ?? throw new NotFoundException();
        await users.UpdateSecurityStampAsync(user);
        await signIn.SignOutAsync();
        return Redirect("/account/login");
    }
}
