using GymTracker.Core.Text;
using GymTracker.Web.Features.Localization;
using GymTracker.Web.Data;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Export;
using GymTracker.Web.Features.Profile;
using GymTracker.Web.Integrations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace GymTracker.Web.Controllers;

[Route("settings")]
public class SettingsController(
    ProfileService profiles,
    DataExport export,
    GeminiKeyStore geminiKeys,
    GeminiFoodParser gemini,
    UserManager<AppUser> users,
    SignInManager<AppUser> signIn,
    Localizer localizer) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Email"] = (await users.GetUserAsync(User))?.Email;
        ViewData["GeminiKey"] = await geminiKeys.StatusAsync(ct);
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
        TempData.Notice("Profile saved.");
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

    [HttpPost("appearance")]
    public async Task<IActionResult> Appearance(GymTracker.Core.Domain.Theme theme, GymTracker.Core.Domain.Language language, CancellationToken ct)
    {
        await profiles.UpdateAsync(p =>
        {
            p.Theme = theme;
            p.Language = language;
        }, ct);
        return Redirect("/settings");
    }

    [HttpPost("gemini-key")]
    public async Task<IActionResult> SetGeminiKey(string? apiKey, CancellationToken ct)
    {
        await geminiKeys.SetAsync(apiKey ?? "", ct);
        await ReportGeminiTestAsync(saved: true, ct);
        return Redirect("/settings");
    }

    [HttpPost("gemini-key/test")]
    public async Task<IActionResult> TestGeminiKey(CancellationToken ct)
    {
        await ReportGeminiTestAsync(saved: false, ct);
        return Redirect("/settings");
    }

    [HttpPost("gemini-key/remove")]
    public async Task<IActionResult> RemoveGeminiKey(CancellationToken ct)
    {
        await geminiKeys.ClearAsync(ct);
        TempData.Notice("Key removed from Settings.");
        return Redirect("/settings");
    }

    private async Task ReportGeminiTestAsync(bool saved, CancellationToken ct)
    {
        if (await geminiKeys.GetAsync(ct) is not { } key)
        {
            TempData.Error("No Gemini key is set.");
            return;
        }

        if (await gemini.TestAsync(key, ct) is { } error)
        {
            // The parser's error is a whole sentence, translated here because TempData carries plain text.
            TempData.Error(new LocalText(saved ? "Key saved, but it doesn't work yet: {0}" : "Key tested, but it doesn't work yet: {0}", localizer[error]));
        }
        else
        {
            TempData.Notice(saved ? "Key saved: Gemini works." : "Key tested: Gemini works.");
        }
    }

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
            // Identity's own descriptions are English only; the errors a user can hit here get our sentences.
            var error = result.Errors.First();
            throw new ValidationException(error.Code switch
            {
                "PasswordMismatch" => "The current password is wrong.",
                "PasswordTooShort" => "The new password needs at least 12 characters.",
                _ => error.Description,
            });
        }

        // Changing the password updates the security stamp; sign in again so this device stays logged in.
        await signIn.SignInAsync(user, isPersistent: true);
        TempData.Notice("Password changed. Other devices will be signed out.");
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
