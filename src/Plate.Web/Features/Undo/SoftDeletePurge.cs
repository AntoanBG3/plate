using Plate.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Features.Undo;

/// <summary>
/// Hourly, permanently deletes rows that were soft-deleted more than a day ago, and old feedback data nobody
/// reported on: parse runs after 180 days and errors not seen for 90, label photos after 30 days and label scans
/// after 180 (SPEC 11).
/// </summary>
public sealed class SoftDeletePurge(IServiceScopeFactory scopes, TimeProvider time, ILogger<SoftDeletePurge> log) : BackgroundService
{
    public static readonly TimeSpan Keep = TimeSpan.FromHours(24);
    public static readonly TimeSpan KeepParseRuns = TimeSpan.FromDays(180);
    public static readonly TimeSpan KeepErrors = TimeSpan.FromDays(90);
    public static readonly TimeSpan KeepAdminAudit = TimeSpan.FromDays(365);
    public static readonly TimeSpan KeepLabelPhotos = TimeSpan.FromDays(30);
    public static readonly TimeSpan KeepLabelScans = TimeSpan.FromDays(180);
    public static readonly TimeSpan KeepUsualFoods = TimeSpan.FromDays(180);

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
        // A day's count goes with its group (cascade), and on its own once it is older than the groups' 90 days.
        count += await db.ErrorEventDays.Where(d => d.Day < DateOnly.FromDateTime(errorCutoff.UtcDateTime)).ExecuteDeleteAsync(ct);
        count += await db.AdminAudit.Where(a => a.At < now - KeepAdminAudit).ExecuteDeleteAsync(ct);
        var photoCutoff = now - KeepLabelPhotos;
        count += await db.LabelScanPhotos.IgnoreQueryFilters().Where(p => p.LabelScan.CreatedAt < photoCutoff).ExecuteDeleteAsync(ct);
        var scanCutoff = now - KeepLabelScans;
        count += await db.LabelScans.IgnoreQueryFilters().Where(s => s.CreatedAt < scanCutoff).ExecuteDeleteAsync(ct);
        count += await db.UsualFoods.IgnoreQueryFilters().Where(u => u.LastUsedAt < now - KeepUsualFoods).ExecuteDeleteAsync(ct);
        return count;
    }
}
