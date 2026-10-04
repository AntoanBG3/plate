using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Undo;

/// <summary>Hourly, permanently deletes rows that were soft-deleted more than a day ago.</summary>
public sealed class SoftDeletePurge(IServiceScopeFactory scopes, TimeProvider time, ILogger<SoftDeletePurge> log) : BackgroundService
{
    public static readonly TimeSpan Keep = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var purged = await PurgeAsync(db, time.GetUtcNow() - Keep, stoppingToken);
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
}
