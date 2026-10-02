using GymTracker.Web.Features.Analytics;
using GymTracker.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace GymTracker.Web.Controllers;

[Route("progress")]
public class ProgressController(TrainingAnalytics analytics) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(int? exerciseId, CancellationToken ct)
    {
        var exercises = await analytics.ExercisesWithHistoryAsync(ct);
        var selected = exercises.FirstOrDefault(e => e.Id == exerciseId) ?? exercises.FirstOrDefault();
        var series = selected is null ? [] : await analytics.E1rmSeriesAsync(selected.Id, ct);
        return View(new ProgressViewModel(exercises, selected, series, await analytics.VolumeAsync(ct)));
    }
}
