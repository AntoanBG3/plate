using System.Text.Json.Nodes;
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Feedback;

public record FeedbackRow(int Id, DateTimeOffset CreatedAt, string Reporter, FeedbackSource Source, FeedbackKind Kind, FeedbackArea Area,
    FeedbackStatus Status, FeedbackRating? Rating, ParseReason Reasons, string? Message, bool HasReply, bool HasScreenshot, int? ParseRunId);

public record FeedbackDetail(FeedbackRow Row, string? Reply, string? FixedInVersion, DateTimeOffset? StatusChangedAt, JsonNode? Context,
    int? ParseItemIndex, ParseRunDetail? ParseRun, ErrorEvent? Error, (string ContentType, int Width, int Height, int Bytes)? Screenshot);

public record ParseRunDetail(int Id, DateTimeOffset CreatedAt, string Reporter, string Text, string? AiModel, string? MatchModel, int ParserMs, int MatchMs,
    string? ParserError, JsonNode? Parsed, JsonNode? Resolved, JsonNode? Saved, ParseCorrection Corrections, DateTimeOffset? ConfirmedAt,
    IReadOnlyList<(int? Item, FeedbackRating? Rating, ParseReason Reasons, string? Message)> Ratings);

/// <summary>One parse as a regression case: the typed text, the parser's items and what the user saved.</summary>
public record EvalCase(int RunId, string Text, JsonNode? Parsed, JsonNode? Saved, string? AiModel, string? MatchModel);

public record Digest(DateTimeOffset Since, IReadOnlyList<FeedbackRow> Reports, IReadOnlyList<ErrorEvent> Errors, int Parses, int Confirmed, int Corrected,
    int RatedUp, int RatedDown);

/// <summary>
/// The CLI's view of feedback across both accounts (SPEC 11). Reads ignore the user filters, so this is never
/// reachable from a web request; only the status, reply and changelog are written.
/// </summary>
public class FeedbackAdmin(AppDbContext db, TimeProvider time)
{
    private IQueryable<Core.Domain.Feedback> AllFeedback => db.Feedback.IgnoreQueryFilters();

    public async Task<List<FeedbackRow>> ListAsync(IReadOnlyCollection<FeedbackStatus>? statuses, DateTimeOffset? since, FeedbackArea? area, bool includeRatings, CancellationToken ct)
    {
        var query = AllFeedback.AsNoTracking();
        if (statuses is { Count: > 0 })
        {
            query = query.Where(f => statuses.Contains(f.Status));
        }

        if (since is { } from)
        {
            query = query.Where(f => f.CreatedAt >= from);
        }

        if (area is { } a)
        {
            query = query.Where(f => f.Area == a);
        }

        if (!includeRatings)
        {
            // A bare thumbs up says nothing to act on; a thumbs down is worth a look, with or without words.
            query = query.Where(f => f.Source != FeedbackSource.ParseRating || f.Rating == FeedbackRating.Down);
        }

        return await Rows(query.OrderByDescending(f => f.CreatedAt).Take(200), ct);
    }

    public async Task<FeedbackDetail?> ShowAsync(int id, CancellationToken ct)
    {
        var row = (await Rows(AllFeedback.AsNoTracking().Where(f => f.Id == id), ct)).SingleOrDefault();
        if (row is null)
        {
            return null;
        }

        var f = await AllFeedback.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        var shot = await db.FeedbackAttachments.IgnoreQueryFilters().Where(a => a.FeedbackId == id)
            .Select(a => new { a.ContentType, a.Width, a.Height, Bytes = a.Bytes.Length })
            .FirstOrDefaultAsync(ct);
        var error = f.ErrorEventId is { } errorId ? await db.ErrorEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == errorId, ct) : null;
        return new FeedbackDetail(row, f.Reply, f.FixedInVersion, f.StatusChangedAt, JsonNode.Parse(f.Context), f.ParseItemIndex,
            f.ParseRunId is { } runId ? await ParseRunAsync(runId, ct) : null, error,
            shot is null ? null : (shot.ContentType, shot.Width, shot.Height, shot.Bytes));
    }

    public async Task<FeedbackAttachment?> AttachmentAsync(int feedbackId, CancellationToken ct) =>
        await db.FeedbackAttachments.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(a => a.FeedbackId == feedbackId, ct);

    /// <summary>Sets the status and, optionally, the reply the reporter sees. Returns false when there is no such report.</summary>
    public async Task<bool> SetStatusAsync(int id, FeedbackStatus status, string? reply, string? version, CancellationToken ct)
    {
        var f = await AllFeedback.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (f is null)
        {
            return false;
        }

        f.Status = status;
        if (!string.IsNullOrWhiteSpace(reply))
        {
            var text = reply.Trim();
            f.Reply = text.Length > Core.Domain.Feedback.MaxReplyLength ? text[..Core.Domain.Feedback.MaxReplyLength] : text;
        }

        if (!string.IsNullOrWhiteSpace(version))
        {
            f.FixedInVersion = version.Trim();
        }

        f.StatusChangedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<ParseRunDetail?> ParseRunAsync(int id, CancellationToken ct)
    {
        var run = await db.ParseRuns.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run is null)
        {
            return null;
        }

        var ratings = await AllFeedback.AsNoTracking()
            .Where(f => f.ParseRunId == id)
            .OrderBy(f => f.CreatedAt)
            .Select(f => new { f.ParseItemIndex, f.Rating, f.Reasons, f.Message })
            .ToListAsync(ct);
        return new ParseRunDetail(run.Id, run.CreatedAt, await ReporterAsync(run.UserId, ct), run.Text, run.AiModel, run.MatchModel, run.ParserMs, run.MatchMs,
            run.ParserError, JsonNode.Parse(run.Parsed), JsonNode.Parse(run.Resolved), run.Saved is null ? null : JsonNode.Parse(run.Saved),
            run.Corrections, run.ConfirmedAt, ratings.Select(r => (r.ParseItemIndex, r.Rating, r.Reasons, r.Message)).ToList());
    }

    /// <summary>Parse runs since a time, newest first; with <paramref name="flaggedOnly"/>, only those corrected or rated down.</summary>
    public async Task<List<ParseRunDetail>> ParsesAsync(DateTimeOffset since, bool flaggedOnly, int limit, CancellationToken ct)
    {
        var rated = AllFeedback.Where(f => f.ParseRunId != null && (f.Rating == FeedbackRating.Down || f.Reasons != ParseReason.None)).Select(f => f.ParseRunId);
        var ids = await db.ParseRuns.IgnoreQueryFilters()
            .Where(r => r.CreatedAt >= since)
            .Where(r => !flaggedOnly || r.Corrections != ParseCorrection.None || rated.Contains(r.Id))
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => r.Id)
            .Take(limit)
            .ToListAsync(ct);
        var result = new List<ParseRunDetail>();
        foreach (var id in ids)
        {
            result.Add((await ParseRunAsync(id, ct))!);
        }

        return result;
    }

    /// <summary>Confirmed parses as regression cases: what was typed, what the parser read and what the user saved.</summary>
    public async Task<List<EvalCase>> EvalCasesAsync(DateTimeOffset since, bool flaggedOnly, CancellationToken ct)
    {
        var runs = await db.ParseRuns.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.CreatedAt >= since && r.Saved != null && (!flaggedOnly || r.Corrections != ParseCorrection.None))
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);
        return runs.Select(r => new EvalCase(r.Id, r.Text, JsonNode.Parse(r.Parsed), JsonNode.Parse(r.Saved!), r.AiModel, r.MatchModel)).ToList();
    }

    public Task<List<ErrorEvent>> ErrorsAsync(DateTimeOffset since, CancellationToken ct) =>
        db.ErrorEvents.AsNoTracking().Where(e => e.LastSeenAt >= since).OrderByDescending(e => e.LastSeenAt).Take(100).ToListAsync(ct);

    public async Task<Digest> DigestAsync(DateTimeOffset since, CancellationToken ct)
    {
        var reports = await ListAsync(null, since, null, includeRatings: false, ct);
        var errors = await db.ErrorEvents.AsNoTracking().Where(e => e.LastSeenAt >= since).OrderByDescending(e => e.Count).ToListAsync(ct);
        var runs = db.ParseRuns.IgnoreQueryFilters().Where(r => r.CreatedAt >= since);
        var ratings = AllFeedback.Where(f => f.Source == FeedbackSource.ParseRating && f.CreatedAt >= since);
        return new Digest(since, reports, errors,
            await runs.CountAsync(ct),
            await runs.CountAsync(r => r.ConfirmedAt != null, ct),
            await runs.CountAsync(r => r.Corrections != ParseCorrection.None, ct),
            await ratings.CountAsync(f => f.Rating == FeedbackRating.Up, ct),
            await ratings.CountAsync(f => f.Rating == FeedbackRating.Down, ct));
    }

    public async Task<ChangelogEntry> AddChangelogAsync(string textEn, string textBg, int? feedbackId, string? version, CancellationToken ct)
    {
        if (feedbackId is { } id && !await AllFeedback.AnyAsync(f => f.Id == id, ct))
        {
            throw new InvalidOperationException($"There is no feedback #{id}.");
        }

        var entry = new ChangelogEntry
        {
            CreatedAt = time.GetUtcNow(),
            TextEn = Clip(textEn),
            TextBg = Clip(textBg),
            FeedbackId = feedbackId,
            Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
        };
        db.Changelog.Add(entry);
        await db.SaveChangesAsync(ct);
        return entry;

        static string Clip(string text) => text.Trim().Length > ChangelogEntry.MaxTextLength ? text.Trim()[..ChangelogEntry.MaxTextLength] : text.Trim();
    }

    private async Task<List<FeedbackRow>> Rows(IQueryable<Core.Domain.Feedback> query, CancellationToken ct)
    {
        var names = db.Profiles.IgnoreQueryFilters();
        return await query
            .Select(f => new FeedbackRow(f.Id, f.CreatedAt, names.Where(p => p.UserId == f.UserId).Select(p => p.DisplayName).FirstOrDefault() ?? "?",
                f.Source, f.Kind, f.Area, f.Status, f.Rating, f.Reasons, f.Message, f.Reply != null, f.Attachment != null, f.ParseRunId))
            .ToListAsync(ct);
    }

    private async Task<string> ReporterAsync(string userId, CancellationToken ct) =>
        await db.Profiles.IgnoreQueryFilters().Where(p => p.UserId == userId).Select(p => p.DisplayName).FirstOrDefaultAsync(ct) ?? "?";
}
