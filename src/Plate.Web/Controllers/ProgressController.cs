using Plate.Web.Features.Analytics;
using Plate.Web.Features.Body;
using Plate.Web.Features.Food;
using Plate.Web.Features.Profile;
using Plate.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Plate.Web.Controllers;

[Route("progress")]
public class ProgressController(TrainingAnalytics analytics, BodyAnalytics body, ProfileService profiles, FoodLogService food, NutritionTargets targets) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(int? exerciseId, string? range, CancellationToken ct)
    {
        var exercises = await analytics.ExercisesWithHistoryAsync(ct);
        var selected = exercises.FirstOrDefault(e => e.Id == exerciseId) ?? exercises.FirstOrDefault();
        var series = selected is null ? [] : await analytics.E1rmSeriesAsync(selected.Id, ct);
        var today = await profiles.TodayAsync(ct);
        var daily = await targets.ForTodayAsync(ct);
        return View(new ProgressViewModel(
            exercises,
            selected,
            series,
            await analytics.VolumeAsync(ct),
            await body.WeightAsync(range, today, ct),
            await body.CaloriesAsync(today, ct),
            await food.WeekCardAsync(today, daily, ct),
            daily.Macros?.ProteinMinG));
    }
}
