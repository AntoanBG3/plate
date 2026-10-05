using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Core.Training;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Body;
using GymTracker.Web.Features.Food;
using GymTracker.Web.Features.Profile;
using GymTracker.Web.Features.Training;
using GymTracker.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace GymTracker.Web.Controllers;

[Route("today")]
public class TodayController(ProfileService profiles, BodyweightService bodyweights, BodyAnalytics body, TemplateService templates, SessionService sessions, FoodLogService food, NutritionTargets targets, GymTracker.Web.Features.Feedback.Changelog changelog) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var profile = await profiles.GetAsync(ct);
        var today = await profiles.TodayAsync(ct);
        if (CutCheck.Sync(profile, today))
        {
            await profiles.UpdateAsync(_ => { }, ct);
        }

        var dailyTargets = await targets.ForTodayAsync(ct);
        var cutChecks = CutCheck.For(profile, today, await bodyweights.WeighInsAsync(ct),
            (await food.WeekCardAsync(today, dailyTargets, ct))?.Week, dailyTargets.Macros);
        var active = await sessions.GetActiveAsync(ct);
        var list = await templates.ListAsync(ct);
        var nextId = NextWorkout.Pick(
            list.Select(t => new TemplateOrder(t.Id, t.SortOrder)).ToList(),
            await sessions.LastCompletedTemplateIdAsync(ct));
        var day = await food.DayAsync(today, ct);
        var weights = await body.WeightAsync("4w", today, ct);
        ViewData["WhatsNew"] = await changelog.UnseenAsync(ct);

        return View(new TodayViewModel(
            profile.DisplayName,
            today,
            await bodyweights.GetForDayAsync(today, ct),
            await bodyweights.LatestAsync(ct),
            active,
            list.FirstOrDefault(t => t.Id == nextId),
            list.Count > 0,
            day.Totals,
            dailyTargets,
            weights.Points.TakeLast(15).ToList(),
            day.Entries.MaxBy(e => e.CreatedAt)?.Description,
            day.Quality,
            cutChecks,
            CutCheck.OnBreak(profile, today) ? profile.DietBreakUntil : null));
    }

    [HttpPost("cut/break")]
    public async Task<IActionResult> StartBreak(CancellationToken ct)
    {
        var today = await profiles.TodayAsync(ct);
        await profiles.UpdateAsync(p =>
        {
            if (p.Goal != Goal.Cut)
            {
                throw new ValidationException("A diet break pauses a cut.");
            }

            CutCheck.StartBreak(p, today);
        }, ct);
        return Redirect("/today");
    }

    [HttpPost("cut/end-break")]
    public async Task<IActionResult> EndBreak(CancellationToken ct)
    {
        var today = await profiles.TodayAsync(ct);
        await profiles.UpdateAsync(p => CutCheck.EndBreak(p, today), ct);
        return Redirect("/today");
    }

    [HttpPost("cut/dismiss")]
    public async Task<IActionResult> DismissCutChecks(CancellationToken ct)
    {
        var today = await profiles.TodayAsync(ct);
        await profiles.UpdateAsync(p => p.CutCheckDismissedUntil = CutCheck.DismissedUntil(today), ct);
        return Redirect("/today");
    }

    [HttpPost("whats-new/dismiss")]
    public async Task<IActionResult> DismissWhatsNew(CancellationToken ct)
    {
        await changelog.DismissAsync(ct);
        return Request.Headers.ContainsKey("HX-Request") ? Content("") : Redirect("/today");
    }

    [HttpPost("weight")]
    public async Task<IActionResult> Weight(decimal weightKg, CancellationToken ct)
    {
        await bodyweights.SetAsync(await profiles.TodayAsync(ct), weightKg, ct);
        return Redirect("/today");
    }
}
