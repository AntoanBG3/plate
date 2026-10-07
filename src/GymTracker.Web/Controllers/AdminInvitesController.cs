using System.Security.Claims;
using GymTracker.Core.Domain;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Admin;
using GymTracker.Web.Features.Localization;
using GymTracker.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GymTracker.Web.Controllers;

/// <summary>The admin area's invites (SPEC 9 and 12): make, list and revoke. English only, like the rest of the area.</summary>
[Route("admin/invites")]
[Authorize(Policy = AdminPolicy.Name)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class AdminInvitesController(InviteAdmin invites) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct) => View(await PageAsync(null, ct));

    /// <summary>Renders the page with the new link instead of redirecting, so the token never passes through TempData.</summary>
    [HttpPost("")]
    [EnableRateLimiting(RateLimits.Admin)]
    public async Task<IActionResult> Create(string? note, int? days, CancellationToken ct)
    {
        var (invite, token) = await invites.CreateAsync(User.FindFirstValue(ClaimTypes.NameIdentifier), note, days ?? InviteRules.DefaultDays, ct);
        return View(nameof(Index), await PageAsync(new NewInvite(invite.Id, invite.Note, $"{Request.Scheme}://{Request.Host}/join/{token}"), ct));
    }

    [HttpPost("{id:int}/revoke")]
    [EnableRateLimiting(RateLimits.Admin)]
    public async Task<IActionResult> Revoke(int id, CancellationToken ct)
    {
        await invites.RevokeAsync(id, ct);
        TempData.Notice(new Core.Text.LocalText("Invite revoked."));
        return Redirect("/admin/invites");
    }

    private async Task<AdminInvitesPage> PageAsync(NewInvite? created, CancellationToken ct) =>
        new(await invites.ListAsync(all: true, ct), created);
}
