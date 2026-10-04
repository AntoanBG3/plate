using System.Text.Json;
using System.Text.Json.Nodes;
using GymTracker.Core.Domain;
using GymTracker.Core.Text;
using GymTracker.Web.Data;
using GymTracker.Web.Features.Diagnostics;
using GymTracker.Web.Features.Profile;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Feedback;

/// <summary>What the browser adds to a report: screen size, whether the app is installed, and its recent errors.</summary>
public record ClientDetails(string? Viewport, bool? Standalone, string? RecentErrors, string? UserAgent);

/// <summary>A screenshot as uploaded; checked by its first bytes, not its declared type.</summary>
public record Screenshot(byte[] Bytes, int Width, int Height);

/// <summary>A report from the Send feedback form or a contextual link.</summary>
public record NewReport(
    Guid ClientId,
    FeedbackKind Kind,
    FeedbackArea Area,
    string? Message,
    FeedbackSource Source,
    int? AboutId,
    string? Page,
    string? TraceId,
    ParseReason Reasons,
    ClientDetails Client,
    Screenshot? Screenshot);

/// <summary>A report's starting choices and a description of what will be attached, for the form.</summary>
public record ReportDraft(FeedbackSource Source, int? AboutId, FeedbackKind Kind, FeedbackArea Area, LocalText? About, bool ShowReasons);

/// <summary>
/// Reports, ideas and parse ratings for the signed-in user (SPEC 11). Everything goes through the user's query
/// filters, so a run, entry or report of someone else's is not found. A repeated ClientId is the same report.
/// </summary>
public class FeedbackService(AppDbContext db, ICurrentUser currentUser, ProfileService profiles, UserTime userTime, TimeProvider time, FeedbackSnapshots snapshots, IFeedbackNotifier notifier)
{
    private const int MaxRecentErrorsLength = 4000;

    private string UserId => currentUser.UserId ?? throw new InvalidOperationException("No signed-in user.");

    /// <summary>A thumbs up or down on a whole parse (item null) or one of its items. One rating per item: a second tap replaces the first.</summary>
    public async Task<int> RateParseAsync(Guid clientId, int runId, int? item, FeedbackRating rating, ClientDetails client, CancellationToken ct)
    {
        if (await db.Feedback.FirstOrDefaultAsync(f => f.ClientId == clientId, ct) is { } repeat)
        {
            return repeat.Id;
        }

        var run = await db.ParseRuns.FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw new NotFoundException("Parse not found.");
        var items = FeedbackJson.Read<List<ResolvedRow>>(run.Resolved)?.Count ?? 0;
        if (item is { } n && (n < 0 || n >= items))
        {
            throw new ValidationException("That food isn't in this list.");
        }

        var existing = await db.Feedback.FirstOrDefaultAsync(f => f.Source == FeedbackSource.ParseRating && f.ParseRunId == runId && f.ParseItemIndex == item, ct);
        if (existing is not null)
        {
            existing.Rating = rating;
            existing.ClientId = clientId;
            if (rating == FeedbackRating.Up)
            {
                existing.Reasons = ParseReason.None;
            }

            await db.SaveChangesAsync(ct);
            return existing.Id;
        }

        var feedback = new Core.Domain.Feedback
        {
            ClientId = clientId,
            UserId = UserId,
            CreatedAt = time.GetUtcNow(),
            Source = FeedbackSource.ParseRating,
            Kind = rating == FeedbackRating.Down ? FeedbackKind.Problem : FeedbackKind.Other,
            Area = FeedbackArea.TextParsing,
            Rating = rating,
            ParseRunId = run.Id,
            ParseItemIndex = item,
            Context = (await ContextAsync("/food/parse", null, client, null, ct)).ToJsonString(FeedbackJson.Options),
        };
        db.Feedback.Add(feedback);
        await SaveNewAsync(ct);
        return feedback.Id;
    }

    /// <summary>Reasons and a comment for a thumbs down, sent after it from the sheet. Notifies, since it now says something.</summary>
    public async Task AddParseDetailsAsync(Guid clientId, ParseReason reasons, string? message, CancellationToken ct)
    {
        var feedback = await db.Feedback.FirstOrDefaultAsync(f => f.ClientId == clientId && f.Source == FeedbackSource.ParseRating, ct)
            ?? throw new NotFoundException("Rating not found.");
        feedback.Reasons = reasons & AllReasons;
        feedback.Message = Clean(message, Core.Domain.Feedback.MaxMessageLength);
        feedback.Area = (reasons & (ParseReason.WrongFood | ParseReason.WrongValues)) != 0 && (reasons & ~(ParseReason.WrongFood | ParseReason.WrongValues)) == 0
            ? FeedbackArea.FoodMatching
            : FeedbackArea.TextParsing;
        await db.SaveChangesAsync(ct);
        notifier.Enqueue(feedback.Id);
    }

    public async Task<int> SubmitAsync(NewReport report, CancellationToken ct)
    {
        if (await db.Feedback.FirstOrDefaultAsync(f => f.ClientId == report.ClientId, ct) is { } repeat)
        {
            return repeat.Id;
        }

        if (!Enum.IsDefined(report.Kind) || !Enum.IsDefined(report.Area) || report.Source == FeedbackSource.ParseRating)
        {
            throw new ValidationException("Pick what this is about.");
        }

        var message = Clean(report.Message, Core.Domain.Feedback.MaxMessageLength);
        if (message is null && report.Screenshot is null && report.Reasons == ParseReason.None)
        {
            throw new ValidationException("Write a few words about it.");
        }

        if (report.Message?.Trim().Length > Core.Domain.Feedback.MaxMessageLength)
        {
            throw new ValidationException(new LocalText("Keep it under {0} characters.", Core.Domain.Feedback.MaxMessageLength));
        }

        var attachment = report.Screenshot is { } shot ? CheckScreenshot(shot) : null;
        var about = await snapshots.ForAsync(report.Source, report.AboutId, ct);
        var feedback = new Core.Domain.Feedback
        {
            ClientId = report.ClientId,
            UserId = UserId,
            CreatedAt = time.GetUtcNow(),
            Source = report.Source,
            Kind = report.Kind,
            Area = report.Area,
            Reasons = report.Source == FeedbackSource.FoodEntry ? report.Reasons & AllReasons : ParseReason.None,
            Rating = report.Source == FeedbackSource.FoodEntry ? FeedbackRating.Down : null,
            Message = message,
            ParseRunId = about.ParseRunId,
            ErrorEventId = report.TraceId is { Length: > 0 } trace
                ? await db.ErrorEvents.Where(e => e.LastTraceId == trace).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct)
                : null,
            Context = (await ContextAsync(report.Page, report.TraceId, report.Client, about.Snapshot, ct)).ToJsonString(FeedbackJson.Options),
            Attachment = attachment,
        };
        db.Feedback.Add(feedback);
        await SaveNewAsync(ct);
        notifier.Enqueue(feedback.Id);
        return feedback.Id;
    }

    /// <summary>The starting choices for a report, and what will be attached to it.</summary>
    public async Task<ReportDraft> DraftAsync(FeedbackSource source, int? aboutId, CancellationToken ct)
    {
        if (source == FeedbackSource.ParseRating)
        {
            source = FeedbackSource.Settings;
        }

        var about = await snapshots.ForAsync(source, aboutId, ct);
        var (kind, area) = source switch
        {
            FeedbackSource.Barcode => (FeedbackKind.Problem, FeedbackArea.NutritionValues),
            FeedbackSource.Progression => (FeedbackKind.Problem, FeedbackArea.Progression),
            FeedbackSource.Maintenance => (FeedbackKind.Problem, FeedbackArea.CaloriesAndMaintenance),
            FeedbackSource.FoodEntry => (FeedbackKind.Problem, FeedbackArea.TextParsing),
            FeedbackSource.ErrorPage => (FeedbackKind.Problem, FeedbackArea.Other),
            _ => (FeedbackKind.Problem, FeedbackArea.Other),
        };
        return new ReportDraft(source, aboutId, kind, area, about.Description, source == FeedbackSource.FoodEntry);
    }

    /// <summary>The user's own reports, newest first, without ratings (they have no status to follow).</summary>
    public Task<List<Core.Domain.Feedback>> MineAsync(CancellationToken ct) =>
        db.Feedback.AsNoTracking()
            .Where(f => f.Source != FeedbackSource.ParseRating || f.Message != null)
            .OrderByDescending(f => f.CreatedAt)
            .Take(100)
            .Select(f => new Core.Domain.Feedback
            {
                Id = f.Id, CreatedAt = f.CreatedAt, Source = f.Source, Kind = f.Kind, Area = f.Area, Rating = f.Rating, Reasons = f.Reasons,
                Message = f.Message, Status = f.Status, Reply = f.Reply, StatusChangedAt = f.StatusChangedAt, ReplySeenAt = f.ReplySeenAt,
                FixedInVersion = f.FixedInVersion,
                Attachment = f.Attachment == null ? null : new FeedbackAttachment { Id = f.Attachment.Id, Width = f.Attachment.Width, Height = f.Attachment.Height },
            })
            .ToListAsync(ct);

    public Task<int> UnreadRepliesAsync(CancellationToken ct) =>
        db.Feedback.CountAsync(f => f.Reply != null && f.StatusChangedAt != null && (f.ReplySeenAt == null || f.ReplySeenAt < f.StatusChangedAt), ct);

    public async Task MarkRepliesSeenAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await db.Feedback
            .Where(f => f.Reply != null && f.StatusChangedAt != null && (f.ReplySeenAt == null || f.ReplySeenAt < f.StatusChangedAt))
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.ReplySeenAt, now), ct);
    }

    public async Task<FeedbackAttachment> AttachmentAsync(int feedbackId, CancellationToken ct) =>
        await db.FeedbackAttachments.AsNoTracking().FirstOrDefaultAsync(a => a.FeedbackId == feedbackId, ct) ?? throw new NotFoundException();

    /// <summary>
    /// A ClientId that is not the user's own but exists (someone else's, or a guess) hits the unique index; refuse it
    /// rather than reveal or touch the other report.
    /// </summary>
    private async Task SaveNewAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            throw new ValidationException("That report can't be saved. Send it again from the form.");
        }
    }

    private const ParseReason AllReasons = ParseReason.WrongFood | ParseReason.WrongAmount | ParseReason.WrongValues | ParseReason.MissedFood
        | ParseReason.ExtraItem | ParseReason.SplitOrMerge | ParseReason.LanguageNotUnderstood;

    /// <summary>JPEG, PNG or WebP by their first bytes, up to the size limit.</summary>
    internal static FeedbackAttachment CheckScreenshot(Screenshot shot)
    {
        if (shot.Bytes.Length > FeedbackAttachment.MaxBytes)
        {
            throw new ValidationException("That picture is too large. Try a screenshot instead of a photo.");
        }

        var type = ImageType(shot.Bytes) ?? throw new ValidationException("Attach a screenshot or photo (JPEG, PNG or WebP).");
        return new FeedbackAttachment
        {
            ContentType = type,
            Width = Math.Clamp(shot.Width, 0, 20000),
            Height = Math.Clamp(shot.Height, 0, 20000),
            Bytes = shot.Bytes,
        };
    }

    internal static string? ImageType(ReadOnlySpan<byte> b) =>
        b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "image/jpeg"
        : b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ? "image/png"
        : b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8) ? "image/webp"
        : null;

    private async Task<JsonObject> ContextAsync(string? page, string? traceId, ClientDetails client, JsonNode? about, CancellationToken ct)
    {
        var profile = await profiles.FindAsync(ct);
        var context = new JsonObject
        {
            ["page"] = Clean(page, 300),
            ["version"] = AppVersion.Current,
            ["userAgent"] = Clean(client.UserAgent, 300),
            ["viewport"] = Clean(client.Viewport, 20),
            ["standalone"] = client.Standalone,
            ["language"] = profile?.Language.ToString(),
            ["theme"] = profile?.Theme.ToString(),
            ["localTime"] = (await userTime.LocalAsync(time.GetUtcNow())).ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            ["traceId"] = Clean(traceId, 100),
            ["recentErrors"] = RecentErrors(client.RecentErrors),
            ["about"] = about,
        };
        foreach (var key in context.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
        {
            context.Remove(key);
        }

        return context;
    }

    /// <summary>The browser's last few errors as sent (a JSON array), or the raw text when it isn't valid JSON.</summary>
    private static JsonNode? RecentErrors(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxRecentErrorsLength)
        {
            return json is { Length: > 0 } ? JsonValue.Create(json[..Math.Min(json.Length, 500)]) : null;
        }

        try
        {
            return JsonNode.Parse(json) is JsonArray { Count: > 0 } array ? array : null;
        }
        catch (JsonException)
        {
            return JsonValue.Create(json);
        }
    }

    private static string? Clean(string? text, int max)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > max ? trimmed[..max] : trimmed;
    }
}
