using GymTracker.Web.Data;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Admin;
using GymTracker.Web.Features.Feedback;
using GymTracker.Web.Integrations;
using GymTracker.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Controllers;

/// <summary>The admin area's dashboard, users and audit pages (SPEC 12). Admin views are English only.</summary>
[Route("admin")]
[Authorize(Policy = AdminPolicy.Name)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class AdminController(
    UserManager<AppUser> users,
    FeedbackAdmin feedback,
    OpenRouterSettings openRouter,
    AdminAuditLog audit,
    AppDbContext db,
    TimeProvider time) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var user = await users.GetUserAsync(User) ?? throw new NotFoundException();
        var models = new List<AiTaskStatus>();
        foreach (var task in Enum.GetValues<AiTask>())
        {
            var model = await openRouter.GetModelAsync(task, ct);
            models.Add(new AiTaskStatus(task, model, null));
        }

        return View(new AdminDashboard(
            await feedback.DigestAsync(time.GetUtcNow().AddDays(-7), ct),
            await openRouter.KeyStatusAsync(ct),
            models,
            await AdminUnlock.ExpiresAsync(HttpContext, user.Id, await users.GetSecurityStampAsync(user), time.GetUtcNow())));
    }

    [HttpPost("lock")]
    public async Task<IActionResult> Lock(CancellationToken ct)
    {
        await AdminUnlock.LockAsync(HttpContext);
        await audit.WriteAsync(AuditActions.Lock, null, null, ct);
        return Redirect("/settings");
    }

    [HttpGet("users")]
    public async Task<IActionResult> Users(CancellationToken ct)
    {
        var devs = (await users.GetUsersInRoleAsync(AdminPolicy.Role)).Select(u => u.Id).ToHashSet();
        var accounts = await db.Users.AsNoTracking().OrderBy(u => u.Email).Select(u => new { u.Id, u.Email }).ToListAsync(ct);
        var profiles = await db.Profiles.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(p => p.UserId, ct);
        var passkeys = await db.UserPasskeys.AsNoTracking().GroupBy(p => p.UserId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var lastReport = await db.Feedback.IgnoreQueryFilters().AsNoTracking().GroupBy(f => f.UserId).Select(g => new { g.Key, Last = g.Max(f => f.CreatedAt) })
            .ToDictionaryAsync(x => x.Key, x => (DateTimeOffset?)x.Last, ct);
        var inviteNotes = await db.Invites.AsNoTracking().Where(i => i.RedeemedByUserId != null)
            .ToDictionaryAsync(i => i.RedeemedByUserId!, i => i.Note ?? "(no note)", ct);
        return View(accounts.Select(a => new AdminUserRow(
            profiles.TryGetValue(a.Id, out var p) ? p.DisplayName : "?",
            a.Email ?? "",
            devs.Contains(a.Id),
            passkeys.GetValueOrDefault(a.Id),
            lastReport.GetValueOrDefault(a.Id),
            inviteNotes.GetValueOrDefault(a.Id))).ToList());
    }

    [HttpGet("audit")]
    public async Task<IActionResult> Audit(CancellationToken ct)
    {
        var entries = await audit.RecentAsync(DateTimeOffset.MinValue, 200, ct);
        var ids = entries.Select(e => e.UserId).OfType<string>().Distinct().ToList();
        var emails = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Email ?? "?", ct);
        return View(entries.Select(e => new AdminAuditRow(e, e.UserId is { } id ? emails.GetValueOrDefault(id) : null)).ToList());
    }
}
