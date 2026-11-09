using System.Text;
using System.Text.Json;
using Plate.Core.Domain;
using Plate.Web.Features;
using Plate.Web.Features.Admin;
using Plate.Web.Features.Diagnostics;
using Plate.Web.Features.Feedback;
using Plate.Web.Features.Localization;
using Plate.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Plate.Web.Controllers;

/// <summary>Reports, parse runs, errors and What's new in the admin area: the feedback CLI on the phone (SPEC 12).</summary>
[Route("admin")]
[Authorize(Policy = AdminPolicy.Name)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class AdminFeedbackController(FeedbackAdmin admin, AdminAuditLog audit, TimeProvider time) : Controller
{
    [HttpGet("feedback")]
    public async Task<IActionResult> List(string? status, string? since, FeedbackArea? area, bool ratings, CancellationToken ct)
    {
        var filter = new AdminFeedbackFilter(status ?? "open", since ?? "30d", area, ratings);
        var statuses = filter.Status switch
        {
            "all" => null,
            "open" => new List<FeedbackStatus> { FeedbackStatus.New, FeedbackStatus.Seen, FeedbackStatus.Planned, FeedbackStatus.NeedsInfo },
            _ => Enum.TryParse<FeedbackStatus>(filter.Status, ignoreCase: true, out var one) ? [one] : throw new NotFoundException(),
        };
        var rows = await admin.ListAsync(statuses, Since(filter.Since), area, ratings, ct);
        return View(new AdminFeedbackList(filter, rows));
    }

    [HttpGet("feedback/{id:int}")]
    public async Task<IActionResult> Report(int id, CancellationToken ct)
    {
        await admin.MarkSeenAsync(id, ct);
        var detail = await admin.ShowAsync(id, ct) ?? throw new NotFoundException();
        return View(detail);
    }

    [HttpPost("feedback/{id:int}/status")]
    [EnableRateLimiting(RateLimits.Admin)]
    public async Task<IActionResult> Status(int id, FeedbackStatus status, string? reply, string? version, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            throw new ValidationException("Pick a status.");
        }

        if (!await admin.SetStatusAsync(id, status, reply, version, ct))
        {
            throw new NotFoundException();
        }

        TempData.Notice(new Core.Text.LocalText("#{0} is now {1}.", id, status.ToString()));
        return Redirect($"/admin/feedback/{id}{(status == FeedbackStatus.Fixed ? "?fixed=1#whats-new" : "")}");
    }

    /// <summary>The screenshot, sandboxed: it is user content, served from the app's own origin.</summary>
    [HttpGet("feedback/{id:int}/screenshot")]
    public async Task<IActionResult> Screenshot(int id, CancellationToken ct)
    {
        var attachment = await admin.AttachmentAsync(id, ct) ?? throw new NotFoundException();
        Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
        return File(attachment.Bytes, attachment.ContentType);
    }

    [HttpGet("parses")]
    public async Task<IActionResult> Parses(bool flagged, string? since, CancellationToken ct) =>
        View(new AdminParseList(flagged, since ?? "30d", await admin.ParsesAsync(Since(since ?? "30d"), flagged, 100, ct)));

    [HttpGet("parses/{id:int}")]
    public async Task<IActionResult> Parse(int id, CancellationToken ct) =>
        View(await admin.ParseRunAsync(id, ct) ?? throw new NotFoundException());

    /// <summary>Regression cases as JSONL (SPEC 11). They hold what users typed: review them before committing any.</summary>
    [HttpGet("parses/export")]
    [EnableRateLimiting(RateLimits.Admin)]
    public async Task<IActionResult> Export(bool flagged, string? since, CancellationToken ct)
    {
        var cases = await admin.EvalCasesAsync(Since(since ?? "30d"), flagged, ct);
        await audit.WriteAsync(AuditActions.ExportCases, null, $"{cases.Count} cases{(flagged ? ", flagged" : "")}, since {since ?? "30d"}", ct);
        var body = new StringBuilder();
        foreach (var c in cases)
        {
            body.Append(JsonSerializer.Serialize(c, FeedbackJson.Options)).Append('\n');
        }

        return File(Encoding.UTF8.GetBytes(body.ToString()), "application/x-ndjson", $"parse-corrections-{time.GetUtcNow():yyyy-MM-dd}.jsonl");
    }

    [HttpGet("errors")]
    public async Task<IActionResult> Errors(string? since, CancellationToken ct) =>
        View(new AdminErrorList(since ?? "7d", await admin.ErrorsAsync(Since(since ?? "7d"), ct)));

    [HttpGet("changelog")]
    public async Task<IActionResult> Changelog(CancellationToken ct) => View(await admin.ChangelogAsync(50, ct));

    [HttpPost("changelog")]
    [EnableRateLimiting(RateLimits.Admin)]
    public async Task<IActionResult> AddChangelog(string? textEn, string? textBg, int? feedbackId, string? version, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(textEn) || string.IsNullOrWhiteSpace(textBg))
        {
            throw new ValidationException("Write the line in both languages.");
        }

        try
        {
            await admin.AddChangelogAsync(textEn, textBg, feedbackId, string.IsNullOrWhiteSpace(version) ? AppVersion.Current : version, ct);
        }
        catch (InvalidOperationException)
        {
            throw new NotFoundException();
        }

        TempData.Notice(new Core.Text.LocalText("Added to What's new."));
        return Redirect("/admin/changelog");
    }

    [HttpPost("changelog/{id:int}/remove")]
    [EnableRateLimiting(RateLimits.Admin)]
    public async Task<IActionResult> RemoveChangelog(int id, CancellationToken ct)
    {
        if (!await admin.RemoveChangelogAsync(id, ct))
        {
            throw new NotFoundException();
        }

        TempData.Notice(new Core.Text.LocalText("Removed from What's new."));
        return Redirect("/admin/changelog");
    }

    /// <summary>"7d", "12h" or a date, as in the CLI; anything else means 30 days.</summary>
    private DateTimeOffset Since(string value) => TimeWindow.SinceOrDefault(value, TimeSpan.FromDays(30), time.GetUtcNow());
}
