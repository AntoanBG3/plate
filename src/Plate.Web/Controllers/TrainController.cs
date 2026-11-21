using Plate.Core.Training;
using Plate.Web.Features;
using Plate.Core.Text;
using Plate.Web.Features.Analytics;
using Plate.Web.Features.Localization;
using Plate.Web.Features.Profile;
using Plate.Web.Features.Training;
using Plate.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Plate.Web.Controllers;

[Route("train")]
public class TrainController(
    SessionService sessions,
    TemplateService templates,
    ExerciseService exercises,
    TrainingAnalytics analytics,
    ExerciseSettingsService settings,
    ProfileService profiles,
    Localizer localizer) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct) =>
        View(new TrainViewModel(await sessions.GetActiveAsync(ct), await templates.ListAsync(ct)));

    [HttpPost("start")]
    public async Task<IActionResult> Start(int? templateId, CancellationToken ct)
    {
        var session = await sessions.StartAsync(templateId, ct);
        return Redirect($"/train/sessions/{session.Id}");
    }

    [HttpGet("history")]
    public async Task<IActionResult> History(CancellationToken ct) =>
        View(await sessions.HistoryAsync(200, ct));

    [HttpGet("sessions/{id:int}")]
    public async Task<IActionResult> Session(int id, CancellationToken ct)
    {
        var session = await sessions.GetAsync(id, ct);
        var guidance = await analytics.GuidanceAsync(session, ct);
        return View(new SessionViewModel(
            session,
            guidance.Suggestions,
            await settings.ForAsync(session.Exercises.Select(e => e.ExerciseId), ct),
            (await profiles.GetAsync(ct)).BarWeightKg,
            guidance.LastTime));
    }

    [HttpGet("sessions/{id:int}/summary")]
    public async Task<IActionResult> Summary(int id, CancellationToken ct)
    {
        var summary = await sessions.SummaryAsync(id, ct);
        return View(new SummaryViewModel(summary, await analytics.RecordsAsync(summary.Session, ct), await analytics.FirstTimesAsync(summary.Session, ct)));
    }

    [HttpPost("sessions/{id:int}/finish")]
    public async Task<IActionResult> Finish(int id, CancellationToken ct)
    {
        await sessions.FinishAsync(id, ct);
        return Redirect($"/train/sessions/{id}/summary");
    }

    [HttpPost("sessions/{id:int}/notes")]
    public async Task<IActionResult> Notes(int id, string? notes, CancellationToken ct)
    {
        await sessions.SetNotesAsync(id, notes, ct);
        return Redirect($"/train/sessions/{id}");
    }

    [HttpPost("sessions/{id:int}/delete")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await sessions.DeleteAsync(id, ct);
        return Redirect("/train/history");
    }

    [HttpGet("sessions/{id:int}/add-exercise")]
    public async Task<IActionResult> AddExercise(int id, CancellationToken ct)
    {
        var session = await sessions.GetAsync(id, ct);
        return View("ExercisePicker", new ExercisePickerViewModel(
            "Add exercise", $"/train/sessions/{id}/exercises", $"/train/sessions/{id}", await exercises.ListAsync(ct),
            Multi: true, AlreadyIn: session.Exercises.Select(e => e.ExerciseId).ToHashSet(), AddTo: $"session:{id}"));
    }

    [HttpPost("sessions/{id:int}/exercises")]
    /// <summary>Adds the picked exercises in the order they were picked (one, from older pages, as exerciseId).</summary>
    public async Task<IActionResult> AddExercise(int id, int? exerciseId, List<int>? exerciseIds, CancellationToken ct)
    {
        var picked = exerciseIds is { Count: > 0 } ? exerciseIds : exerciseId is { } one ? [one] : [];
        var (first, duplicate) = await sessions.AddExercisesAsync(id, picked, ct);
        if (duplicate is not null)
        {
            TempData.Notice(new LocalText("Added a second {0}.", localizer.Exercise(duplicate)));
        }

        if (first is { } f)
        {
            TempData.Focus($"ex-{f}");
        }

        return Redirect($"/train/sessions/{id}");
    }

    [HttpGet("sessions/{id:int}/reorder")]
    public async Task<IActionResult> Reorder(int id, CancellationToken ct)
    {
        var session = await sessions.GetAsync(id, ct);
        return View("Reorder", new ReorderViewModel($"/train/sessions/{id}/order", $"/train/sessions/{id}",
            session.Exercises.Select(e => (e.Id, localizer.Exercise(e.Exercise))).ToList()));
    }

    [HttpPost("sessions/{id:int}/order")]
    public async Task<IActionResult> Order(int id, List<int> ids, CancellationToken ct)
    {
        await sessions.ReorderAsync(id, ids, ct);
        return Redirect($"/train/sessions/{id}");
    }

    [HttpGet("session-exercises/{id:int}/swap")]
    public async Task<IActionResult> Swap(int id, CancellationToken ct)
    {
        var item = await sessions.GetExerciseAsync(id, ct);
        return View("ExercisePicker", new ExercisePickerViewModel(
            new LocalText("Swap {0}", localizer.Exercise(item.Exercise)),
            $"/train/session-exercises/{id}/swap",
            $"/train/sessions/{item.SessionId}",
            ExerciseOrder.ForSwap(await exercises.ListAsync(ct), item.Exercise)));
    }

    [HttpPost("session-exercises/{id:int}/swap")]
    public async Task<IActionResult> Swap(int id, int exerciseId, CancellationToken ct)
    {
        var sessionId = await sessions.SwapExerciseAsync(id, exerciseId, ct);
        TempData.Focus($"ex-{id}");
        return Redirect($"/train/sessions/{sessionId}");
    }

    [HttpPost("session-exercises/{id:int}/move")]
    public async Task<IActionResult> MoveExercise(int id, int direction, CancellationToken ct)
    {
        var sessionId = await sessions.MoveExerciseAsync(id, direction, ct);
        TempData.Focus($"ex-{id}");
        return Redirect($"/train/sessions/{sessionId}");
    }

    [HttpPost("session-exercises/{id:int}/remove")]
    public async Task<IActionResult> RemoveExercise(int id, CancellationToken ct)
    {
        var sessionId = await sessions.RemoveExerciseAsync(id, ct);
        TempData.Undo("Exercise removed.", $"/undo/session-exercise/{id}");
        return Redirect($"/train/sessions/{sessionId}");
    }

    /// <summary>
    /// The plate calculator sheet. Opened from the card it starts from the last working set, else the suggestion;
    /// once the sheet's own field sends (typed), only what is in the field counts, so clearing it asks for a weight.
    /// </summary>
    [HttpGet("session-exercises/{id:int}/plates")]
    [RedisplayInvalidNumbers]
    public async Task<IActionResult> Plates(int id, decimal? weight, decimal? suggested, bool typed, CancellationToken ct)
    {
        var exercise = await sessions.GetExerciseAsync(id, ct);
        var profile = await profiles.GetAsync(ct);
        var bar = PlateMath.BarFor(exercise.Exercise.Equipment, (await settings.GetAsync(exercise.ExerciseId, ct))?.BarWeightKg, profile.BarWeightKg)
            ?? throw new NotFoundException();
        var target = typed ? weight : weight ?? await sessions.LastWorkingWeightAsync(id, ct) ?? suggested;
        var load = target is > 0 and <= SetRules.MaxWeightKg ? PlateMath.PerSide(target.Value, bar, profile.PlatesKg) : null;
        return PartialView("_Plates", new PlatesViewModel(id, bar, target, load));
    }

    /// <summary>Adds an empty row for an extra working set or a warmup, stored on the session exercise; htmx appends it.</summary>
    [HttpPost("session-exercises/{id:int}/slots")]
    public async Task<IActionResult> AddSlot(int id, bool warmup, CancellationToken ct)
    {
        var exercise = await sessions.AddSlotAsync(id, warmup, ct);

        // An extra working set starts from the last one logged.
        var last = warmup ? null : exercise.Sets.Where(s => !s.IsWarmup).MaxBy(s => s.SetNumber);
        var target = last is null ? null : new Plate.Core.Training.SetTarget(last.WeightKg, last.Reps);
        return PartialView("_SetRow", SetRowViewModel.Blank(id, warmup, target));
    }

    /// <summary>Removes an empty row (swiped away). An empty response takes the row off the page; 400 when it can't go.</summary>
    [HttpPost("session-exercises/{id:int}/slots/remove")]
    public async Task<IActionResult> RemoveSlot(int id, bool warmup, CancellationToken ct) =>
        await sessions.RemoveSlotAsync(id, warmup, ct)
            ? Content("")
            : throw new ValidationException(warmup ? "There is no empty warm-up row to remove." : "An exercise keeps at least one set.");

    [HttpPost("sets")]
    public async Task<IActionResult> LogSet(LogSetForm form, CancellationToken ct)
    {
        var set = await sessions.LogSetAsync(
            new LogSetInput(form.ClientId, form.SessionExerciseId, form.WeightKg ?? 0, form.Reps ?? 0, form.Rpe, form.IsWarmup, form.CompletedAt),
            ct);
        return PartialView("_SetRow", SetRowViewModel.AfterLog(set));
    }

    [HttpPost("sets/{id:int}/update")]
    public async Task<IActionResult> UpdateSet(int id, decimal? weightKg, int? reps, decimal? rpe, CancellationToken ct) =>
        PartialView("_SetRow", SetRowViewModel.For(await sessions.UpdateSetAsync(id, weightKg ?? 0, reps ?? 0, rpe, ct)) with { JustSaved = true });

    [HttpPost("sets/{id:int}/delete")]
    public async Task<IActionResult> DeleteSet(int id, CancellationToken ct)
    {
        var set = await sessions.DeleteSetAsync(id, ct);

        // htmx turns this header into a "gym:undo" event; motion.js shows the toast. The text is a script string (t()).
        Response.Headers["HX-Trigger"] = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["gym:undo"] = new { text = "Set deleted.", url = $"/undo/set/{id}" },
        });
        return PartialView("_SetRow", SetRowViewModel.Blank(set.SessionExerciseId, set.IsWarmup));
    }

    public record LogSetForm(Guid ClientId, int SessionExerciseId, decimal? WeightKg, int? Reps, decimal? Rpe, bool IsWarmup, DateTimeOffset? CompletedAt);
}
