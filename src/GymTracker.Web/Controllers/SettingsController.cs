using GymTracker.Web.Data;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Export;
using GymTracker.Web.Features.Profile;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace GymTracker.Web.Controllers;

[Route("settings")]
public class SettingsController(ProfileService profiles, DataExport export, UserManager<AppUser> users, SignInManager<AppUser> signIn) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Email"] = (await users.GetUserAsync(User))?.Email;
        return View(await profiles.GetAsync(ct));
    }

    [HttpPost("profile")]
    public async Task<IActionResult> Profile(ProfileForm form, CancellationToken ct)
    {
        await profiles.UpdateAsync(p =>
        {
            p.DisplayName = form.DisplayName?.Trim() ?? "";
            p.TimeZoneId = string.IsNullOrWhiteSpace(form.TimeZoneId) ? GymTracker.Core.Domain.UserProfile.DefaultTimeZoneId : form.TimeZoneId.Trim();
            p.Sex = form.Sex;
            p.HeightCm = form.HeightCm;
            p.BirthYear = form.BirthYear;
            p.InitialMaintenanceKcal = form.InitialMaintenanceKcal;
            p.Goal = form.Goal;
            p.TargetRatePercentPerWeek = form.TargetRatePercentPerWeek ?? 0;
            p.ProteinGramsPerKg = form.ProteinGramsPerKg ?? 2.0m;
            p.DefaultWeightIncrementKg = form.DefaultWeightIncrementKg ?? 2.5m;
        }, ct);
        TempData["Message"] = "Profile saved.";
        return Redirect("/settings");
    }

    public record ProfileForm(
        string? DisplayName,
        string? TimeZoneId,
        GymTracker.Core.Domain.Sex? Sex,
        int? HeightCm,
        int? BirthYear,
        int? InitialMaintenanceKcal,
        GymTracker.Core.Domain.Goal Goal,
        decimal? TargetRatePercentPerWeek,
        decimal? ProteinGramsPerKg,
        decimal? DefaultWeightIncrementKg);

    [HttpGet("export")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var today = await profiles.TodayAsync(ct);
        return File(await export.ZipAsync(ct), "application/zip", $"gymtracker-{today:yyyy-MM-dd}.zip");
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
