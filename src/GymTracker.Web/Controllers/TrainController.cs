using GymTracker.Core.Training;
using GymTracker.Web.Features;
using GymTracker.Core.Text;
using GymTracker.Web.Features.Analytics;
using GymTracker.Web.Features.Localization;
using GymTracker.Web.Features.Profile;
using GymTracker.Web.Features.Training;
using GymTracker.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace GymTracker.Web.Controllers;

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
        return View(new SessionViewModel(
            session,
            await analytics.SuggestionsAsync(session, ct),
            await settings.ForAsync(session.Exercises.Select(e => e.ExerciseId), ct),
            (await profiles.GetAsync(ct)).BarWeightKg));
    }

    [HttpGet("sessions/{id:int}/summary")]
    public async Task<IActionResult> Summary(int id, CancellationToken ct)
    {
        var summary = await sessions.SummaryAsync(id, ct);
        return View(new SummaryViewModel(summary, await analytics.RecordsAsync(summary.Session, ct)));
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
        await sessions.GetAsync(id, ct);
        return View("ExercisePicker", new ExercisePickerViewModel(
            "Add exercise", $"/train/sessions/{id}/exercises", $"/train/sessions/{id}", await exercises.ListAsync(ct)));
    }

    [HttpPost("sessions/{id:int}/exercises")]
    public async Task<IActionResult> AddExercise(int id, int exerciseId, CancellationToken ct)
    {
        await sessions.AddExerciseAsync(id, exerciseId, ct);
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
    public async Task<IActionResult> Swap(int id, int exerciseId, CancellationToken ct) =>
        Redirect($"/train/sessions/{await sessions.SwapExerciseAsync(id, exerciseId, ct)}");

    [HttpPost("session-exercises/{id:int}/move")]
    public async Task<IActionResult> MoveExercise(int id, int direction, CancellationToken ct) =>
        Redirect($"/train/sessions/{await sessions.MoveExerciseAsync(id, direction, ct)}");

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

    /// <summary>An empty row for an extra working set or a warmup, appended by htmx.</summary>
    [HttpGet("session-exercises/{id:int}/row")]
    public async Task<IActionResult> BlankRow(int id, bool warmup, CancellationToken ct)
    {
        var exercise = await sessions.GetExerciseAsync(id, ct);

        // An extra working set starts from the last one logged.
        var last = warmup ? null : exercise.Sets.Where(s => !s.IsWarmup).MaxBy(s => s.SetNumber);
        var target = last is null ? null : new GymTracker.Core.Training.SetTarget(last.WeightKg, last.Reps);
        return PartialView("_SetRow", SetRowViewModel.Blank(id, warmup, target));
    }

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
