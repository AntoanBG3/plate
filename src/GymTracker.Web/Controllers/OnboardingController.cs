using GymTracker.Web.Features;
using GymTracker.Web.Features.Localization;
using GymTracker.Web.Features.Onboarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GymTracker.Web.Controllers;

/// <summary>The first sign-in setup: a splash, the chat, and the summary or plain form (SPEC 6.4).</summary>
[Route("welcome")]
public class OnboardingController(OnboardingService onboarding) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(bool restart, bool form, string? returnUrl, CancellationToken ct)
    {
        ViewData["ReturnUrl"] = Url.SafeReturnUrl(returnUrl);
        ViewData["ShowForm"] = form;
        return View(await onboarding.PageAsync(restart, ct));
    }

    [HttpPost("start")]
    [EnableRateLimiting(RateLimits.AiOnboarding)]
    public async Task<IActionResult> Start(string? returnUrl, CancellationToken ct)
    {
        if (await onboarding.StartAsync(ct) is { } error)
        {
            TempData.Error(error);
        }

        return Redirect(Here(returnUrl));
    }

    [HttpPost("message")]
    [EnableRateLimiting(RateLimits.AiOnboarding)]
    public async Task<IActionResult> Message(string? text, string? returnUrl, CancellationToken ct)
    {
        if (await onboarding.SendAsync(text, ct) is { } error)
        {
            TempData.Error(error);
        }

        return Redirect(Here(returnUrl));
    }

    [HttpPost("finish")]
    public async Task<IActionResult> Finish(OnboardingForm form, string? returnUrl, CancellationToken ct)
    {
        await onboarding.FinishAsync(form, ct);
        TempData.Notice("You're all set.");
        return Redirect(Url.SafeReturnUrl(returnUrl));
    }

    [HttpPost("skip")]
    public async Task<IActionResult> Skip(string? returnUrl, CancellationToken ct)
    {
        await onboarding.SkipAsync(ct);
        return Redirect(Url.SafeReturnUrl(returnUrl));
    }

    private string Here(string? returnUrl)
    {
        var target = Url.SafeReturnUrl(returnUrl);
        return target == "/today" ? "/welcome" : "/welcome?returnUrl=" + Uri.EscapeDataString(target);
    }
}
