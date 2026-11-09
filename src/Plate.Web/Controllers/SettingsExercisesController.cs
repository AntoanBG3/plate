using Plate.Core.Domain;
using Plate.Web.Features;
using Plate.Web.Features.Localization;
using Plate.Web.Features.Training;
using Plate.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Plate.Web.Controllers;

/// <summary>The user's custom exercises (SPEC 7 Settings). Creating one from the picker adds it where the picker was.</summary>
[Route("settings/exercises")]
public class SettingsExercisesController(ExerciseService exercises, TemplateService templates, SessionService sessions) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct) => View(await exercises.ListOwnAsync(ct));

    /// <param name="addTo">"template:{id}" or "session:{id}" when opened from that picker; the new exercise goes there.</param>
    [HttpGet("new")]
    public IActionResult New(string? name, string? addTo) =>
        View("Edit", new CustomExerciseViewModel(null, name?.Trim() ?? "", Muscle.Chest, Equipment.Barbell, [], AddTo(addTo)));

    [HttpPost("")]
    public async Task<IActionResult> Create(string name, Muscle primaryMuscle, Equipment equipment, List<Muscle>? secondaryMuscles, string? addTo, CancellationToken ct)
    {
        var exercise = await exercises.CreateAsync(name, primaryMuscle, equipment, secondaryMuscles ?? [], ct);
        switch (AddTo(addTo))
        {
            case ("template", var id):
                TempData.Focus($"te-{await templates.AddExerciseAsync(id, exercise.Id, ct)}");
                return Redirect($"/train/templates/{id}");
            case ("session", var id):
                TempData.Focus($"ex-{await sessions.AddExerciseAsync(id, exercise.Id, ct)}");
                return Redirect($"/train/sessions/{id}");
            default:
                return Redirect("/settings/exercises");
        }
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var exercise = (await exercises.ListOwnAsync(ct)).FirstOrDefault(e => e.Id == id) ?? throw new NotFoundException();
        return View(new CustomExerciseViewModel(exercise.Id, exercise.Name, exercise.PrimaryMuscle, exercise.Equipment, exercise.SecondaryMuscles, null));
    }

    [HttpPost("{id:int}/rename")]
    public async Task<IActionResult> Rename(int id, string name, CancellationToken ct)
    {
        await exercises.RenameAsync(id, name, ct);
        return Redirect("/settings/exercises");
    }

    [HttpPost("{id:int}/archive")]
    public async Task<IActionResult> Archive(int id, CancellationToken ct)
    {
        await exercises.ArchiveAsync(id, ct);
        return Redirect("/settings/exercises");
    }

    private static (string Kind, int Id)? AddTo(string? value) =>
        value?.Split(':') is [var kind and ("template" or "session"), var id] && int.TryParse(id, out var n) ? (kind, n) : null;
}
