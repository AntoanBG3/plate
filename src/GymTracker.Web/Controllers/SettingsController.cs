using GymTracker.Core.Text;
using GymTracker.Web.Features.Localization;
using GymTracker.Web.Data;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Export;
using GymTracker.Web.Features.Profile;
using GymTracker.Web.Integrations;
using GymTracker.Web.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GymTracker.Web.Controllers;

[Route("settings")]
public class SettingsController(
    ProfileService profiles,
    GymTracker.Web.Features.Food.NutritionTargets targets,
    DataExport export,
    OpenRouterSettings openRouter,
    UserManager<AppUser> users,
    SignInManager<AppUser> signIn,
    GymTracker.Web.Features.Feedback.FeedbackService feedback) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var user = await users.GetUserAsync(User);
        ViewData["Email"] = user?.Email;
        ViewData["Passkeys"] = user is null ? [] : await users.GetPasskeysAsync(user);
        ViewData["OpenRouter"] = await OpenRouterStatusAsync(ct);
        ViewData["UnreadFeedback"] = await feedback.UnreadRepliesAsync(ct);
        ViewData["Targets"] = await targets.ForTodayAsync(ct);
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
            p.ActivityLevel = form.ActivityLevel;
            p.InitialMaintenanceKcal = form.InitialMaintenanceKcal;
            p.Goal = form.Goal;
            p.TargetRatePercentPerWeek = form.TargetRatePercentPerWeek ?? 0;
            p.ProteinGramsPerKg = form.ProteinGramsPerKg;
            p.DefaultWeightIncrementKg = form.DefaultWeightIncrementKg ?? 2.5m;
            p.BarWeightKg = form.BarWeightKg ?? 20m;
            p.PlatesKg = (form.PlatesKg ?? []).Distinct().OrderDescending().ToList();
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
        GymTracker.Core.Domain.ActivityLevel? ActivityLevel,
        int? InitialMaintenanceKcal,
        GymTracker.Core.Domain.Goal Goal,
        decimal? TargetRatePercentPerWeek,
        decimal? ProteinGramsPerKg,
        decimal? DefaultWeightIncrementKg,
        decimal? BarWeightKg,
        List<decimal>? PlatesKg);

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

    private async Task<OpenRouterStatus> OpenRouterStatusAsync(CancellationToken ct)
    {
        var tasks = new List<AiTaskStatus>();
        foreach (var task in Enum.GetValues<AiTask>())
        {
            var model = await openRouter.GetModelAsync(task, ct);
            tasks.Add(new AiTaskStatus(task, model, await openRouter.FindModelAsync(task, model, ct)));
        }

        return new OpenRouterStatus(await openRouter.KeyStatusAsync(ct), tasks);
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var today = await profiles.TodayAsync(ct);
        return File(await export.ZipAsync(ct), "application/zip", $"gymtracker-{today:yyyy-MM-dd}.zip");
    }

    [HttpPost("password")]
    [EnableRateLimiting(RateLimits.Sensitive)]
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
        await GymTracker.Web.Features.Admin.AdminUnlock.LockAsync(HttpContext);
        return Redirect("/account/login");
    }
}
