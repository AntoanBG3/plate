using GymTracker.Core.Domain;
using GymTracker.Core.Text;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Feedback;
using GymTracker.Web.Features.Localization;
using GymTracker.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GymTracker.Web.Controllers;

/// <summary>Reports, ideas and parse ratings (SPEC 11). The browser's offline queue sends with X-Gym-Queue and gets JSON back.</summary>
[Route("feedback")]
public class FeedbackController(FeedbackService feedback, ErrorRecorder errors) : Controller
{
    private const string QueueHeader = "X-Gym-Queue";
    private const int MaxClientErrorBytes = 8 * 1024;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var mine = await feedback.MineAsync(ct);
        await feedback.MarkRepliesSeenAsync(ct);
        return View(mine);
    }

    [HttpGet("new")]
    public async Task<IActionResult> New(FeedbackSource? about, int? id, string? trace, CancellationToken ct)
    {
        var draft = await feedback.DraftAsync(about ?? FeedbackSource.Settings, id, ct);
        return View(new FeedbackFormViewModel(draft, Guid.NewGuid(), Clip(trace, 100), RefererPath()));
    }

    [HttpPost("")]
    [EnableRateLimiting(RateLimits.Feedback)]
    public async Task<IActionResult> Submit(
        Guid clientId, FeedbackKind kind, FeedbackArea area, string? message, FeedbackSource source, int? aboutId, string? page, string? traceId,
        List<ParseReason>? reasons, string? viewport, bool? standalone, string? recentErrors, IFormFile? screenshot, int screenshotWidth, int screenshotHeight,
        CancellationToken ct)
    {
        // An unknown kind or area binds as the enum's first value; refuse it instead.
        if (ModelState.TryGetValue(nameof(kind), out var k) && k.Errors.Count > 0 || ModelState.TryGetValue(nameof(area), out var a) && a.Errors.Count > 0)
        {
            throw new ValidationException("Pick what this is about.");
        }

        var report = new NewReport(
            clientId == Guid.Empty ? Guid.NewGuid() : clientId, kind, area, message, source, aboutId, page, traceId,
            (reasons ?? []).Aggregate(ParseReason.None, (all, r) => all | r),
            Client(viewport, standalone, recentErrors),
            await ReadAsync(screenshot, screenshotWidth, screenshotHeight, ct));
        var id = await feedback.SubmitAsync(report, ct);
        if (Request.Headers.ContainsKey(QueueHeader))
        {
            return Json(new { id });
        }

        TempData.Notice(new LocalText("Thanks. The answer will show here."));
        return Redirect("/feedback");
    }

    [HttpPost("parse/{runId:int}")]
    [EnableRateLimiting(RateLimits.Feedback)]
    public async Task<IActionResult> RateParse(int runId, Guid clientId, FeedbackRating rating, int? item, string? viewport, bool? standalone, CancellationToken ct)
    {
        if (clientId == Guid.Empty || !Enum.IsDefined(rating))
        {
            throw new ValidationException("That rating wasn't valid.");
        }

        return Json(new { id = await feedback.RateParseAsync(clientId, runId, item, rating, Client(viewport, standalone, null), ct) });
    }

    [HttpPost("parse/details")]
    [EnableRateLimiting(RateLimits.Feedback)]
    public async Task<IActionResult> ParseDetails(Guid clientId, List<ParseReason>? reasons, string? message, CancellationToken ct)
    {
        await feedback.AddParseDetailsAsync(clientId, (reasons ?? []).Aggregate(ParseReason.None, (all, r) => all | r), message, ct);
        return NoContent();
    }

    /// <summary>A browser error caught by feedback.js. Small and rate limited; recorded like a server error.</summary>
    [HttpPost("client-error")]
    [EnableRateLimiting(RateLimits.ClientError)]
    [RequestSizeLimit(MaxClientErrorBytes)]
    public async Task<IActionResult> ClientError(string? message, string? stack, string? page, string? version, CancellationToken ct)
    {
        // Also checked here: the test server and some proxies don't apply the request size limit.
        if (Request.ContentLength > MaxClientErrorBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return NoContent();
        }

        var (type, text) = ErrorRecorder.SplitClientMessage(message);
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        await errors.RecordAsync(new ErrorReport(GymTracker.Core.Domain.ErrorOrigin.Client, type, text, stack, page, userId, null,
            string.IsNullOrWhiteSpace(version) ? null : version), ct);
        return NoContent();
    }

    [HttpGet("{id:int}/screenshot")]
    public async Task<IActionResult> Screenshot(int id, CancellationToken ct)
    {
        var attachment = await feedback.AttachmentAsync(id, ct);
        Response.Headers.CacheControl = "private, max-age=86400";
        return File(attachment.Bytes, attachment.ContentType);
    }

    private ClientDetails Client(string? viewport, bool? standalone, string? recentErrors) =>
        new(viewport, standalone, recentErrors, Request.Headers.UserAgent.ToString());

    private static async Task<Screenshot?> ReadAsync(IFormFile? file, int width, int height, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return null;
        }

        if (file.Length > Core.Domain.FeedbackAttachment.MaxBytes)
        {
            throw new ValidationException("That picture is too large. Try a screenshot instead of a photo.");
        }

        using var buffer = new MemoryStream((int)file.Length);
        await file.CopyToAsync(buffer, ct);
        return new Screenshot(buffer.ToArray(), width, height);
    }

    /// <summary>The page the user came from, path and query only, when it is on this site.</summary>
    private string? RefererPath() =>
        Uri.TryCreate(Request.Headers.Referer.ToString(), UriKind.Absolute, out var uri) && uri.Host == Request.Host.Host && uri.PathAndQuery != "/feedback/new"
            ? Clip(uri.PathAndQuery, 300)
            : null;

    private static string? Clip(string? text, int max) => text is { Length: > 0 } t ? t.Length > max ? t[..max] : t : null;
}
