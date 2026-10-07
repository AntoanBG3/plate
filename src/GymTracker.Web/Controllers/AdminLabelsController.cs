using GymTracker.Web.Features;
using GymTracker.Web.Features.Admin;
using GymTracker.Web.Features.Feedback;
using GymTracker.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GymTracker.Web.Controllers;

/// <summary>Label scans in the admin area: the photo beside what the model read and what was saved (SPEC 12).</summary>
[Route("admin/labels")]
[Authorize(Policy = AdminPolicy.Name)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class AdminLabelsController(LabelScanReport labels, TimeProvider time) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> List(string? since, bool edited, bool failed, CancellationToken ct)
    {
        since ??= "30d";
        return View(new AdminLabelList(since, edited, failed, await labels.ListAsync(Since(since), edited, failed, 100, ct)));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Scan(int id, CancellationToken ct) =>
        View(await labels.GetAsync(id, ct) ?? throw new NotFoundException());

    /// <summary>The photo, sandboxed like a report's screenshot: it is user content served from the app's own origin.</summary>
    [HttpGet("{id:int}/photo")]
    public async Task<IActionResult> Photo(int id, CancellationToken ct)
    {
        var photo = await labels.PhotoAsync(id, ct) ?? throw new NotFoundException();
        Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
        return File(photo.Bytes, photo.ContentType);
    }

    private DateTimeOffset Since(string value)
    {
        try
        {
            return Cli.FeedbackCommand.Since(value, TimeSpan.FromDays(30))!.Value;
        }
        catch (FormatException)
        {
            return time.GetUtcNow().AddDays(-30);
        }
    }
}
