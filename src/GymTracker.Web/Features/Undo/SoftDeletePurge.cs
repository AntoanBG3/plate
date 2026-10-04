using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Undo;

/// <summary>
/// Hourly, permanently deletes rows that were soft-deleted more than a day ago, and old feedback data nobody
/// reported on: parse runs after 180 days and errors not seen for 90 (SPEC 11).
/// </summary>
public sealed class SoftDeletePurge(IServiceScopeFactory scopes, TimeProvider time, ILogger<SoftDeletePurge> log) : BackgroundService
{
    public static readonly TimeSpan Keep = TimeSpan.FromHours(24);
    public static readonly TimeSpan KeepParseRuns = TimeSpan.FromDays(180);
    public static readonly TimeSpan KeepErrors = TimeSpan.FromDays(90);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = time.GetUtcNow();
                var purged = await PurgeAsync(db, now - Keep, stoppingToken);
                purged += await PurgeFeedbackAsync(db, now, stoppingToken);
                if (purged > 0)
                {
                    log.LogInformation("Purged {Count} soft-deleted rows.", purged);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Soft-delete purge failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Runs without a user, so it bypasses every filter; it only touches rows deleted before the cutoff.</summary>
    public static async Task<int> PurgeAsync(AppDbContext db, DateTimeOffset cutoff, CancellationToken ct)
    {
        var count = await db.Sets.IgnoreQueryFilters().Where(x => x.DeletedAt < cutoff).ExecuteDeleteAsync(ct);
        count += await db.SessionExercises.IgnoreQueryFilters().Where(x => x.DeletedAt < cutoff).ExecuteDeleteAsync(ct);
        count += await db.TemplateExercises.IgnoreQueryFilters().Where(x => x.DeletedAt < cutoff).ExecuteDeleteAsync(ct);
        count += await db.FoodLog.IgnoreQueryFilters().Where(x => x.DeletedAt < cutoff).ExecuteDeleteAsync(ct);
        return count;
    }

    /// <summary>
    /// Parse runs that no report points to, and error groups that stopped happening. Saved food entries keep their
    /// numbers; only their link to the run is cleared (set null by the foreign key).
    /// </summary>
    public static async Task<int> PurgeFeedbackAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var runCutoff = now - KeepParseRuns;
        var reported = db.Feedback.IgnoreQueryFilters().Where(f => f.ParseRunId != null).Select(f => f.ParseRunId);
        var count = await db.ParseRuns.IgnoreQueryFilters()
            .Where(r => r.CreatedAt < runCutoff && !reported.Contains(r.Id))
            .ExecuteDeleteAsync(ct);
        var errorCutoff = now - KeepErrors;
        var linked = db.Feedback.IgnoreQueryFilters().Where(f => f.ErrorEventId != null).Select(f => f.ErrorEventId);
        count += await db.ErrorEvents.Where(e => e.LastSeenAt < errorCutoff && !linked.Contains(e.Id)).ExecuteDeleteAsync(ct);
        return count;
    }
}
