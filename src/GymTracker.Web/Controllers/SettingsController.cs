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
    DataExport export,
    OpenRouterSettings openRouter,
    OpenRouterFoodParser parser,
    JevFoodMatchDecider matcher,
    UserManager<AppUser> users,
    SignInManager<AppUser> signIn,
    GymTracker.Web.Features.Feedback.FeedbackService feedback,
    Localizer localizer) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var user = await users.GetUserAsync(User);
        ViewData["Email"] = user?.Email;
        ViewData["Passkeys"] = user is null ? [] : await users.GetPasskeysAsync(user);
        ViewData["OpenRouter"] = await OpenRouterStatusAsync(ct);
        ViewData["UnreadFeedback"] = await feedback.UnreadRepliesAsync(ct);
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

    [HttpPost("openrouter-key")]
    [EnableRateLimiting(RateLimits.Sensitive)]
    public async Task<IActionResult> SetOpenRouterKey(string? apiKey, CancellationToken ct)
    {
        await openRouter.SetKeyAsync(apiKey ?? "", ct);
        await ReportTestAsync(new LocalText("Key saved: food parsing works."), new LocalText("Key saved, but it doesn't work yet: {0}"), ct);
        return Redirect("/settings");
    }

    [HttpPost("openrouter-key/test")]
    [EnableRateLimiting(RateLimits.Sensitive)]
    public async Task<IActionResult> TestOpenRouterKey(CancellationToken ct)
    {
        await ReportTestAsync(new LocalText("Key tested: food parsing works."), new LocalText("Key tested, but it doesn't work yet: {0}"), ct);
        return Redirect("/settings");
    }

    [HttpPost("openrouter-key/remove")]
    [EnableRateLimiting(RateLimits.Sensitive)]
    public async Task<IActionResult> RemoveOpenRouterKey(CancellationToken ct)
    {
        await openRouter.ClearKeyAsync(ct);
        TempData.Notice("Key removed from Settings.");
        return Redirect("/settings");
    }

    [HttpGet("model/{task}")]
    public async Task<IActionResult> Model(string task, CancellationToken ct) =>
        AiTasks.FromSlug(task) is { } t
            ? View(new ModelPickerViewModel(t, await openRouter.ListModelsAsync(t, ct), await openRouter.GetModelAsync(t, ct)))
            : NotFound();

    [HttpPost("model/{task}")]
    public async Task<IActionResult> SetModel(string task, string? modelId, CancellationToken ct)
    {
        if (AiTasks.FromSlug(task) is not { } t)
        {
            return NotFound();
        }

        await openRouter.SetModelAsync(t, modelId ?? "", ct);
        if (await openRouter.GetKeyAsync(ct) is not { } key || (t == AiTask.Matcher && modelId == AiTasks.Off))
        {
            TempData.Notice("Model saved.");
        }
        else if (t == AiTask.Parser)
        {
            await ReportTestAsync(new LocalText("Model saved: food parsing works."), new LocalText("Model saved, but it doesn't work yet: {0}"), ct);
        }
        else
        {
            Report(await matcher.TestAsync(key, modelId!, ct), new LocalText("Model saved: food matching works."), new LocalText("Model saved, but it doesn't work yet: {0}"));
        }

        return Redirect("/settings");
    }

    /// <summary>Parses "1 banana" with the current key and model and reports the outcome as a notice or error.</summary>
    private async Task ReportTestAsync(LocalText works, LocalText fails, CancellationToken ct)
    {
        if (await openRouter.GetKeyAsync(ct) is not { } key)
        {
            TempData.Error("No OpenRouter key is set.");
            return;
        }

        Report(await parser.TestAsync(key, await openRouter.GetModelAsync(AiTask.Parser, ct), ct), works, fails);
    }

    private void Report(LocalText? error, LocalText works, LocalText fails)
    {
        if (error is not null)
        {
            // The error is a whole sentence, translated here because TempData carries plain text.
            TempData.Error(fails with { Args = [localizer[error]] });
        }
        else
        {
            TempData.Notice(works);
        }
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
        return Redirect("/account/login");
    }
}
