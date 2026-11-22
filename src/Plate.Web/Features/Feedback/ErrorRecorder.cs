using System.Diagnostics;
using System.Text.RegularExpressions;
using Plate.Core.Domain;
using Plate.Core.Text;
using Plate.Web.Data;
using Plate.Web.Features.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Features.Feedback;

/// <summary>One error as reported, before grouping.</summary>
public record ErrorReport(ErrorOrigin Origin, string Type, string Message, string? Stack, string? Path, string? UserId, string? TraceId, string? Version);

/// <summary>
/// Records unhandled server exceptions and browser errors, one row per fingerprint with a count (SPEC 11).
/// Uses its own DbContext, since the request's may be what failed, and never throws.
/// </summary>
public partial class ErrorRecorder(IServiceScopeFactory scopes, TimeProvider time, IFeedbackNotifier notifier, ILogger<ErrorRecorder> log)
{
    public async Task RecordAsync(ErrorReport report, CancellationToken ct)
    {
        // Not errors, and pages cached before the browser stopped sending them still do (Phase 11).
        if (Plate.Core.Text.ErrorNoise.IsIgnored(report.Message))
        {
            return;
        }

        try
        {
            var fingerprint = ErrorFingerprint.Of(report.Type, report.Message, report.Stack, report.Path);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = time.GetUtcNow();
                var existing = await db.ErrorEvents.Where(e => e.Fingerprint == fingerprint).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
                if (existing is { } id)
                {
                    // In the database, not read-modify-write: two requests failing at once each count.
                    var (traceId, version, path) = (Clip(report.TraceId, 100), Clip(report.Version, 40), Clip(report.Path, 300));
                    await db.ErrorEvents.Where(e => e.Id == id).ExecuteUpdateAsync(u => u
                        .SetProperty(e => e.Count, e => e.Count + 1)
                        .SetProperty(e => e.LastSeenAt, now)
                        .SetProperty(e => e.LastTraceId, traceId)
                        .SetProperty(e => e.LastVersion, version)
                        .SetProperty(e => e.UserId, e => report.UserId ?? e.UserId)
                        .SetProperty(e => e.Path, path), ct);
                    await CountDayAsync(db, id, now, ct);
                    return;
                }

                var error = new ErrorEvent
                {
                    Fingerprint = fingerprint,
                    Origin = report.Origin,
                    Type = Clip(report.Type, 200) ?? "Error",
                    Message = Clip(report.Message, 1000) ?? "",
                    Path = Clip(report.Path, 300),
                    Stack = Clip(report.Stack, 4000),
                    UserId = report.UserId,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                    Count = 1,
                    LastTraceId = Clip(report.TraceId, 100),
                    FirstVersion = Clip(report.Version, 40),
                    LastVersion = Clip(report.Version, 40),
                };
                db.ErrorEvents.Add(error);
                try
                {
                    await db.SaveChangesAsync(ct);
                    await CountDayAsync(db, error.Id, now, ct);
                    notifier.EnqueueError(error.Id);
                    return;
                }
                catch (DbUpdateException) when (attempt == 0)
                {
                    // The same error was recorded at the same moment by another request: count it on that row.
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not record an error.");
        }
    }

    /// <summary>One more on the group's UTC day (P10), atomically, so concurrent reports never lose a count.</summary>
    private static Task CountDayAsync(AppDbContext db, int errorEventId, DateTimeOffset now, CancellationToken ct)
    {
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ErrorEventDays" ("ErrorEventId", "Day", "Count") VALUES ({errorEventId}, {day}, 1)
            ON CONFLICT ("ErrorEventId", "Day") DO UPDATE SET "Count" = "ErrorEventDays"."Count" + 1
            """, ct);
    }

    /// <summary>A browser error's type from its message: "Uncaught TypeError: x is undefined" → TypeError.</summary>
    public static (string Type, string Message) SplitClientMessage(string message)
    {
        var text = message.StartsWith("Uncaught ", StringComparison.Ordinal) ? message[9..] : message;
        var match = ErrorType().Match(text);
        return match.Success ? (match.Groups[1].Value, text[(match.Length)..].Trim()) : ("Error", text);
    }

    private static string? Clip(string? text, int max) => text is { Length: > 0 } t ? t.Length > max ? t[..max] : t : null;

    [GeneratedRegex(@"^(\w*Error|\w*Exception):")]
    private static partial Regex ErrorType();
}

/// <summary>Records unhandled exceptions, then lets the error page handle the response.</summary>
public sealed class ErrorCaptureHandler(ErrorRecorder recorder) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            return false; // the phone went away mid-request
        }

        var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var trace = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        // By now the request path is already the error page's; the feature keeps the page that failed.
        var path = Redact(context.Features.Get<IExceptionHandlerFeature>()?.Path ?? context.Request.Path.Value);
        await recorder.RecordAsync(new ErrorReport(ErrorOrigin.Server, exception.GetType().Name, exception.Message, exception.ToString(),
            path, userId, trace, AppVersion.Current), CancellationToken.None);
        return false;
    }

    /// <summary>An invite link's path is its secret (SPEC 9), so the error log keeps only the route.</summary>
    public static string? Redact(string? path) =>
        path is not null && path.StartsWith("/join/", StringComparison.OrdinalIgnoreCase) ? "/join/{token}" : path;
}
