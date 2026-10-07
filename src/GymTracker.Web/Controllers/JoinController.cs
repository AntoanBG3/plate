using GymTracker.Core.Domain;
using GymTracker.Web.Data;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Localization;
using GymTracker.Web.Features.Users;
using GymTracker.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;

namespace GymTracker.Web.Controllers;

/// <summary>Creating an account from a single-use invite link (SPEC 9). The only anonymous page besides login.</summary>
[Route("join")]
[AllowAnonymous]
[EnableRateLimiting(RateLimits.Join)]
[LanguageFromRequest]
public class JoinController(InviteService invites, SignInManager<AppUser> signIn) : Controller
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        Response.Headers["X-Robots-Tag"] = "noindex";
        base.OnActionExecuting(context);
    }

    [HttpGet("{token}")]
    public async Task<IActionResult> Index(string token, CancellationToken ct) =>
        View(new JoinViewModel(token, await StateAsync(token, ct), HttpContext.Preferences().Language));

    [HttpPost("{token}")]
    public async Task<IActionResult> Index(string token, string? name, string? email, string? password, CancellationToken ct)
    {
        var language = HttpContext.Preferences().Language;
        if (await StateAsync(token, ct) is not JoinState.Form and var state)
        {
            return View(new JoinViewModel(token, state, language));
        }

        var result = await invites.RedeemAsync(token, name ?? "", email ?? "", password ?? "", language, ct);
        switch (result.Status)
        {
            case RedemptionStatus.Redeemed:
                await signIn.SignInAsync(result.User!, isPersistent: true);
                // Today, which sends a new account on to the setup chat.
                return Redirect("/");
            case RedemptionStatus.Failed:
                return View(new JoinViewModel(token, JoinState.Form, language, name, email, result.Errors));
            default:
                // Used up between the check and the claim (another tab, or a race).
                return View(new JoinViewModel(token, JoinState.Invalid, language));
        }
    }

    private async Task<JoinState> StateAsync(string token, CancellationToken ct) =>
        User.Identity?.IsAuthenticated == true ? JoinState.SignedIn
        : await invites.IsOpenAsync(token, ct) ? JoinState.Form
        : JoinState.Invalid;
}
