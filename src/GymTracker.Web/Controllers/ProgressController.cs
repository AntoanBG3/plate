using GymTracker.Web.Features.Analytics;
using GymTracker.Web.Features.Body;
using GymTracker.Web.Features.Profile;
using GymTracker.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace GymTracker.Web.Controllers;

[Route("progress")]
public class ProgressController(TrainingAnalytics analytics, BodyAnalytics body, ProfileService profiles) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(int? exerciseId, string? range, CancellationToken ct)
    {
        var exercises = await analytics.ExercisesWithHistoryAsync(ct);
        var selected = exercises.FirstOrDefault(e => e.Id == exerciseId) ?? exercises.FirstOrDefault();
        var series = selected is null ? [] : await analytics.E1rmSeriesAsync(selected.Id, ct);
        var today = await profiles.TodayAsync(ct);
        return View(new ProgressViewModel(
            exercises,
            selected,
            series,
            await analytics.VolumeAsync(ct),
            await body.WeightAsync(range, today, ct),
            await body.CaloriesAsync(today, ct)));
    }
}
