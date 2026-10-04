using GymTracker.Core.Text;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Admin;
using GymTracker.Web.Features.Feedback;
using GymTracker.Web.Features.Localization;
using GymTracker.Web.Integrations;
using GymTracker.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GymTracker.Web.Controllers;

/// <summary>The shared OpenRouter key and AI models, and the parse evaluation, in the admin area (SPEC 12).</summary>
[Route("admin/ai")]
[Authorize(Policy = AdminPolicy.Name)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class AdminAiController(OpenRouterSettings openRouter, AiAdmin ai, ModelAccuracyReport accuracy, EvalJobs evals, AdminAuditLog audit, Localizer localizer, TimeProvider time)
    : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var tasks = new List<AiTaskStatus>();
        foreach (var task in Enum.GetValues<AiTask>())
        {
            var model = await openRouter.GetModelAsync(task, ct);
            tasks.Add(new AiTaskStatus(task, model, await openRouter.FindModelAsync(task, model, ct)));
        }

        return View(new OpenRouterStatus(await openRouter.KeyStatusAsync(ct), tasks));
    }

    [HttpPost("key")]
    [EnableRateLimiting(RateLimits.Sensitive)]
    public async Task<IActionResult> SetKey(string? apiKey, CancellationToken ct)
    {
        await ai.SetKeyAsync(apiKey ?? "", ct);
        Report(await ai.TestParserAsync(ct), "Key saved: food parsing works.", "Key saved, but it doesn't work yet: {0}");
        return Redirect("/admin/ai");
    }

    [HttpPost("key/test")]
    [EnableRateLimiting(RateLimits.Sensitive)]
    public async Task<IActionResult> TestKey(CancellationToken ct)
    {
        Report(await ai.TestParserAsync(ct), "Key tested: food parsing works.", "Key tested, but it doesn't work yet: {0}");
        return Redirect("/admin/ai");
    }

    [HttpPost("key/remove")]
    [EnableRateLimiting(RateLimits.Sensitive)]
    public async Task<IActionResult> RemoveKey(CancellationToken ct)
    {
        await ai.ClearKeyAsync(ct);
        TempData.Notice("Key removed from Settings.");
        return Redirect("/admin/ai");
    }

    [HttpGet("model/{task}")]
    public async Task<IActionResult> Model(string task, CancellationToken ct) =>
        AiTasks.FromSlug(task) is { } t
            ? View(new ModelPickerViewModel(t, await openRouter.ListModelsAsync(t, ct), await openRouter.GetModelAsync(t, ct), await accuracy.ForAsync(t, ct)))
            : NotFound();

    [HttpPost("model/{task}")]
    [EnableRateLimiting(RateLimits.Admin)]
    public async Task<IActionResult> SetModel(string task, string? modelId, CancellationToken ct)
    {
        if (AiTasks.FromSlug(task) is not { } t)
        {
            return NotFound();
        }

        await ai.SetModelAsync(t, modelId ?? "", ct);
        if (await openRouter.GetKeyAsync(ct) is null || (t == AiTask.Matcher && modelId == AiTasks.Off))
        {
            TempData.Notice("Model saved.");
        }
        else if (t == AiTask.Parser)
        {
            Report(await ai.TestParserAsync(ct), "Model saved: food parsing works.", "Model saved, but it doesn't work yet: {0}");
        }
        else
        {
            Report(await ai.TestMatcherAsync(ct), "Model saved: food matching works.", "Model saved, but it doesn't work yet: {0}");
        }

        return Redirect("/admin/ai");
    }

    [HttpGet("eval")]
    public async Task<IActionResult> Eval(CancellationToken ct) =>
        View(new AdminEvalPage(evals.Current, await openRouter.GetModelAsync(AiTask.Parser, ct), await openRouter.GetModelAsync(AiTask.Matcher, ct),
            await openRouter.GetKeyAsync(ct) is not null));

    /// <summary>The progress block, polled by the Eval page while a run is going.</summary>
    [HttpGet("eval/status")]
    public IActionResult EvalStatus() => PartialView("_EvalStatus", evals.Current);

    [HttpPost("eval")]
    [EnableRateLimiting(RateLimits.Admin)]
    public async Task<IActionResult> StartEval(string? since, bool flagged, int? limit, string? parser, string? matcher, CancellationToken ct)
    {
        if (await openRouter.GetKeyAsync(ct) is null)
        {
            throw new ValidationException("No OpenRouter key is set.");
        }

        // Only models the pickers offer: the parser from OpenRouter's catalog, the matcher off or Jev.
        parser = string.IsNullOrWhiteSpace(parser) ? null : parser.Trim();
        if (parser is not null && await openRouter.FindModelAsync(AiTask.Parser, parser, ct) is null)
        {
            throw new ValidationException("That model can't parse food.");
        }

        matcher = matcher is "off" or "jev" ? matcher : null;
        var sinceDate = time.GetUtcNow().AddDays(-(since is "30d" ? 30 : since is "90d" ? 90 : 365));
        var request = new EvalRequest(sinceDate, flagged, Math.Clamp(limit ?? 10, 1, EvalJobs.MaxLimit), parser, matcher);
        if (!evals.TryStart(request))
        {
            throw new ValidationException("An evaluation is already running.");
        }

        await audit.WriteAsync(AuditActions.Eval, null, $"limit {request.Limit}, since {since ?? "365d"}{(flagged ? ", flagged" : "")}, parser {parser ?? "current"}, matcher {matcher ?? "current"}", ct);
        return Redirect("/admin/ai/eval");
    }

    private void Report(LocalText? error, string works, string fails)
    {
        if (error is null)
        {
            TempData.Notice(works);
        }
        else
        {
            // The error is a whole sentence, translated here because TempData carries plain text.
            TempData.Error(new LocalText(fails, localizer[error]));
        }
    }
}
