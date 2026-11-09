using Plate.Web.Features;
using Plate.Web.Features.Profile;
using Plate.Web.Features.Training;
using Plate.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Plate.Web.Controllers;

[Route("train/exercises")]
public class ExercisesController(ExerciseService exercises, ExerciseSettingsService settings, ProfileService profiles) : Controller
{
    /// <summary>The Notes and bar form, loaded into the exercise sheet.</summary>
    [HttpGet("{id:int}/settings")]
    public async Task<IActionResult> Settings(int id, string? returnUrl, CancellationToken ct) =>
        PartialView("_ExerciseSettingsForm", new ExerciseSettingsFormViewModel(
            await exercises.GetAsync(id, ct), await settings.GetAsync(id, ct), (await profiles.GetAsync(ct)).BarWeightKg, Url.SafeReturnUrl(returnUrl)));

    [HttpPost("{id:int}/settings")]
    public async Task<IActionResult> Save(int id, string? note, decimal? barWeightKg, string? returnUrl, CancellationToken ct)
    {
        await settings.SaveAsync(id, note, barWeightKg, ct);
        return Redirect(Url.SafeReturnUrl(returnUrl));
    }
}
