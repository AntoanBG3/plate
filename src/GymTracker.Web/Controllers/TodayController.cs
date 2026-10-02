using GymTracker.Core.Training;
using GymTracker.Web.Features.Body;
using GymTracker.Web.Features.Food;
using GymTracker.Web.Features.Profile;
using GymTracker.Web.Features.Training;
using GymTracker.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace GymTracker.Web.Controllers;

[Route("today")]
public class TodayController(ProfileService profiles, BodyweightService bodyweights, TemplateService templates, SessionService sessions, FoodLogService food, NutritionTargets targets) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var profile = await profiles.GetAsync(ct);
        var today = await profiles.TodayAsync(ct);
        var active = await sessions.GetActiveAsync(ct);
        var list = await templates.ListAsync(ct);
        var nextId = NextWorkout.Pick(
            list.Select(t => new TemplateOrder(t.Id, t.SortOrder)).ToList(),
            await sessions.LastCompletedTemplateIdAsync(ct));

        return View(new TodayViewModel(
            profile.DisplayName,
            today,
            await bodyweights.GetForDayAsync(today, ct),
            await bodyweights.LatestAsync(ct),
            active,
            list.FirstOrDefault(t => t.Id == nextId),
            list.Count > 0,
            (await food.DayAsync(today, ct)).Totals,
            await targets.ForTodayAsync(ct)));
    }

    [HttpPost("weight")]
    public async Task<IActionResult> Weight(decimal weightKg, CancellationToken ct)
    {
        await bodyweights.SetAsync(await profiles.TodayAsync(ct), weightKg, ct);
        return Redirect("/today");
    }
}
