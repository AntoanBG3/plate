using GymTracker.Web.Data;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Profile;
using GymTracker.Web.Features.Training;
using GymTracker.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Controllers;

[Route("train/exercises")]
public class ExercisesController(ExerciseSettingsService settings, ProfileService profiles, AppDbContext db) : Controller
{
    /// <summary>The Notes and bar form, loaded into the exercise sheet.</summary>
    [HttpGet("{id:int}/settings")]
    public async Task<IActionResult> Settings(int id, string? returnUrl, CancellationToken ct)
    {
        // The exercise query filter limits this to the shared library and the user's own exercises.
        var exercise = await db.Exercises.SingleOrDefaultAsync(e => e.Id == id, ct) ?? throw new NotFoundException("Exercise not found.");
        return PartialView("_ExerciseSettingsForm", new ExerciseSettingsFormViewModel(
            exercise, await settings.GetAsync(id, ct), (await profiles.GetAsync(ct)).BarWeightKg, Url.SafeReturnUrl(returnUrl)));
    }

    [HttpPost("{id:int}/settings")]
    public async Task<IActionResult> Save(int id, string? note, decimal? barWeightKg, string? returnUrl, CancellationToken ct)
    {
        await settings.SaveAsync(id, note, barWeightKg, ct);
        return Redirect(Url.SafeReturnUrl(returnUrl));
    }
}
