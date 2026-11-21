using Plate.Core.Text;
using Plate.Web.Features;
using Plate.Web.Features.Localization;
using Plate.Web.Features.Profile;
using Plate.Web.Features.Training;
using Plate.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Plate.Web.Controllers;

[Route("train/templates")]
public class TemplatesController(TemplateService templates, ExerciseService exercises, ExerciseSettingsService settings, ProfileService profiles, Localizer localizer) : Controller
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
        return View(new TemplateEditViewModel(template, await settings.ForAsync(template.Exercises.Select(e => e.ExerciseId), ct), (await profiles.GetAsync(ct)).DefaultWeightIncrementKg));
    }

    [HttpPost("{id:int}/rename")]
    public async Task<IActionResult> Rename(int id, string name, CancellationToken ct)
    {
        if (Request.Headers.ContainsKey("HX-Request"))
        {
            try
            {
                await templates.RenameAsync(id, name, ct);
                var saved = (await templates.GetAsync(id, ct)).Name;
                // site.js puts the new name in the page's titles.
                Response.Headers["HX-Trigger"] = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["gym:renamed"] = new { name = saved } });
                return PartialView("_TemplateNameForm", new TemplateNameFormViewModel(id, saved, Saved: true));
            }
            catch (ValidationException ex)
            {
                return PartialView("_TemplateNameForm", new TemplateNameFormViewModel(id, name ?? "", ex.Text));
            }
        }

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

    [HttpGet("{id:int}/reorder")]
    public async Task<IActionResult> Reorder(int id, CancellationToken ct)
    {
        var template = await templates.GetAsync(id, ct);
        return View("Reorder", new ReorderViewModel($"/train/templates/{id}/order", $"/train/templates/{id}",
            template.Exercises.OrderBy(e => e.SortOrder).Select(e => (e.Id, localizer.Exercise(e.Exercise))).ToList()));
    }

    [HttpPost("{id:int}/order")]
    public async Task<IActionResult> Order(int id, List<int> ids, CancellationToken ct)
    {
        await templates.ReorderAsync(id, ids, ct);
        return Redirect($"/train/templates/{id}");
    }

    [HttpGet("{id:int}/add-exercise")]
    public async Task<IActionResult> AddExercise(int id, CancellationToken ct)
    {
        var template = await templates.GetAsync(id, ct);
        return View("ExercisePicker", new ExercisePickerViewModel(
            new Plate.Core.Text.LocalText("Add to {0}", template.Name), $"/train/templates/{id}/exercises", $"/train/templates/{id}", await exercises.ListAsync(ct),
            Multi: true, AlreadyIn: template.Exercises.Select(e => e.ExerciseId).ToHashSet(), AddTo: $"template:{id}"));
    }

    [HttpPost("{id:int}/exercises")]
    /// <summary>Adds the picked exercises in the order they were picked (one, from older pages, as exerciseId).</summary>
    public async Task<IActionResult> AddExercise(int id, int? exerciseId, List<int>? exerciseIds, CancellationToken ct)
    {
        var picked = exerciseIds is { Count: > 0 } ? exerciseIds : exerciseId is { } one ? [one] : [];
        var (first, duplicate) = await templates.AddExercisesAsync(id, picked, ct);
        if (duplicate is not null)
        {
            TempData.Notice(new LocalText("Added a second {0}.", localizer.Exercise(duplicate)));
        }

        if (first is { } f)
        {
            TempData.Focus($"te-{f}");
        }

        return Redirect($"/train/templates/{id}");
    }

    [HttpPost("exercises/{id:int}")]
    public async Task<IActionResult> UpdateExercise(int id, int workingSets, int repMin, int repMax, decimal? weightIncrementKg, int restSeconds, CancellationToken ct)
    {
        var input = new TemplateExerciseInput(workingSets, repMin, repMax, weightIncrementKg, restSeconds);
        if (Request.Headers.ContainsKey("HX-Request"))
        {
            // Saved on change from the page: send the form back, marked saved, or with the error and what was typed.
            try
            {
                await templates.UpdateExerciseAsync(id, input, ct);
                return PartialView("_TemplateExerciseForm", new TemplateExerciseFormViewModel(await templates.GetExerciseAsync(id, ct), (await profiles.GetAsync(ct)).DefaultWeightIncrementKg, Saved: true));
            }
            catch (ValidationException ex)
            {
                return PartialView("_TemplateExerciseForm", new TemplateExerciseFormViewModel(await templates.GetExerciseAsync(id, ct), (await profiles.GetAsync(ct)).DefaultWeightIncrementKg, input, ex.Text));
            }
        }

        await templates.UpdateExerciseAsync(id, input, ct);
        TempData.Focus($"te-{id}");
        return Redirect($"/train/templates/{await templates.TemplateIdOfExerciseAsync(id, ct)}");
    }

    [HttpPost("exercises/{id:int}/move")]
    public async Task<IActionResult> MoveExercise(int id, int direction, CancellationToken ct)
    {
        await templates.MoveExerciseAsync(id, direction, ct);
        TempData.Focus($"te-{id}");
        return Redirect($"/train/templates/{await templates.TemplateIdOfExerciseAsync(id, ct)}");
    }

    [HttpPost("exercises/{id:int}/remove")]
    public async Task<IActionResult> RemoveExercise(int id, CancellationToken ct)
    {
        var templateId = await templates.TemplateIdOfExerciseAsync(id, ct);
        await templates.RemoveExerciseAsync(id, ct);
        TempData.Undo("Exercise removed.", $"/undo/template-exercise/{id}");
        return Redirect($"/train/templates/{templateId}");
    }
}
