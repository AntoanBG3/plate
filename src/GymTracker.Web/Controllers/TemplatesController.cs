using GymTracker.Core.Text;
using GymTracker.Web.Features.Localization;
using GymTracker.Web.Features.Training;
using GymTracker.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace GymTracker.Web.Controllers;

[Route("train/templates")]
public class TemplatesController(TemplateService templates, ExerciseService exercises, ExerciseSettingsService settings, Localizer localizer) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct) => View(await templates.ListAsync(ct));

    [HttpPost("")]
    public async Task<IActionResult> Create(string name, CancellationToken ct)
    {
        var template = await templates.CreateAsync(name, ct);
        return Redirect($"/train/templates/{template.Id}");
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var template = await templates.GetAsync(id, ct);
        return View(new TemplateEditViewModel(template, await settings.ForAsync(template.Exercises.Select(e => e.ExerciseId), ct)));
    }

    [HttpPost("{id:int}/rename")]
    public async Task<IActionResult> Rename(int id, string name, CancellationToken ct)
    {
        await templates.RenameAsync(id, name, ct);
        return Redirect($"/train/templates/{id}");
    }

    [HttpPost("{id:int}/archive")]
    public async Task<IActionResult> Archive(int id, CancellationToken ct)
    {
        await templates.ArchiveAsync(id, ct);
        return Redirect("/train/templates");
    }

    [HttpPost("{id:int}/move")]
    public async Task<IActionResult> Move(int id, int direction, CancellationToken ct)
    {
        await templates.MoveAsync(id, direction, ct);
        return Redirect("/train/templates");
    }

    [HttpPost("{id:int}/duplicate")]
    public async Task<IActionResult> Duplicate(int id, CancellationToken ct)
    {
        var copy = await templates.DuplicateAsync(id, name => localizer[new LocalText("{0} (copy)", name)], ct);
        return Redirect($"/train/templates/{copy.Id}");
    }

    [HttpGet("{id:int}/add-exercise")]
    public async Task<IActionResult> AddExercise(int id, CancellationToken ct)
    {
        var template = await templates.GetAsync(id, ct);
        return View("ExercisePicker", new ExercisePickerViewModel(
            new GymTracker.Core.Text.LocalText("Add to {0}", template.Name), $"/train/templates/{id}/exercises", $"/train/templates/{id}", await exercises.ListAsync(ct)));
    }

    [HttpPost("{id:int}/exercises")]
    public async Task<IActionResult> AddExercise(int id, int exerciseId, CancellationToken ct)
    {
        await templates.AddExerciseAsync(id, exerciseId, ct);
        return Redirect($"/train/templates/{id}");
    }

    [HttpPost("exercises/{id:int}")]
    public async Task<IActionResult> UpdateExercise(int id, int workingSets, int repMin, int repMax, decimal? weightIncrementKg, int restSeconds, CancellationToken ct)
    {
        await templates.UpdateExerciseAsync(id, new TemplateExerciseInput(workingSets, repMin, repMax, weightIncrementKg, restSeconds), ct);
        return Redirect($"/train/templates/{await templates.TemplateIdOfExerciseAsync(id, ct)}");
    }

    [HttpPost("exercises/{id:int}/move")]
    public async Task<IActionResult> MoveExercise(int id, int direction, CancellationToken ct)
    {
        await templates.MoveExerciseAsync(id, direction, ct);
        return Redirect($"/train/templates/{await templates.TemplateIdOfExerciseAsync(id, ct)}");
    }

    [HttpPost("exercises/{id:int}/remove")]
    public async Task<IActionResult> RemoveExercise(int id, CancellationToken ct)
    {
        var templateId = await templates.TemplateIdOfExerciseAsync(id, ct);
        await templates.RemoveExerciseAsync(id, ct);
        return Redirect($"/train/templates/{templateId}");
    }
}
