using GymTracker.Core.Text;
using GymTracker.Web.Features;
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

    [HttpGet("{id:int}/add-exercise")]
    public async Task<IActionResult> AddExercise(int id, CancellationToken ct)
    {
        var template = await templates.GetAsync(id, ct);
        return View("ExercisePicker", new ExercisePickerViewModel(
            new GymTracker.Core.Text.LocalText("Add to {0}", template.Name), $"/train/templates/{id}/exercises", $"/train/templates/{id}", await exercises.ListAsync(ct),
            Multi: true, AlreadyIn: template.Exercises.Select(e => e.ExerciseId).ToHashSet(), AddTo: $"template:{id}"));
    }

    [HttpPost("{id:int}/exercises")]
    /// <summary>Adds the picked exercises in the order they were picked (one, from older pages, as exerciseId).</summary>
    public async Task<IActionResult> AddExercise(int id, int? exerciseId, List<int>? exerciseIds, CancellationToken ct)
    {
        var picked = exerciseIds is { Count: > 0 } ? exerciseIds : exerciseId is { } one ? [one] : [];
        var before = (await templates.GetAsync(id, ct)).Exercises.Select(e => e.ExerciseId).ToHashSet();
        int? first = null;
        foreach (var pick in picked)
        {
            var added = await templates.AddExerciseAsync(id, pick, ct);
            first ??= added;
        }

        await NoteSecondCopyAsync(picked, before, ct);
        if (first is { } f)
        {
            TempData.Focus($"te-{f}");
        }

        return Redirect($"/train/templates/{id}");
    }

    /// <summary>Picking an exercise that is already there adds a second one; say so, as that may be a slip.</summary>
    private async Task NoteSecondCopyAsync(List<int> picked, HashSet<int> before, CancellationToken ct)
    {
        if (picked.FirstOrDefault(before.Contains) is var again and > 0)
        {
            TempData.Notice(new LocalText("Added a second {0}.", localizer.Exercise(await exercises.GetAsync(again, ct))));
        }
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
                return PartialView("_TemplateExerciseForm", new TemplateExerciseFormViewModel(await templates.GetExerciseAsync(id, ct), Saved: true));
            }
            catch (ValidationException ex)
            {
                return PartialView("_TemplateExerciseForm", new TemplateExerciseFormViewModel(await templates.GetExerciseAsync(id, ct), input, ex.Text));
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
