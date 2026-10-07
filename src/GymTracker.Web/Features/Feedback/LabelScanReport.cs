using GymTracker.Core.Domain;
using GymTracker.Web.Data;
using GymTracker.Web.Features.Admin;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Feedback;

public record LabelScanRow(int Id, DateTimeOffset CreatedAt, string Reporter, string Model, int LatencyMs, string? Error, bool Saved, bool Edited, bool HasPhoto);

public record LabelScanDetail(LabelScanRow Scan, string? Reading, string? Values, string? Saved, int? FoodItemId);

public record LabelScanCounts(int Reads, int Failed, int Edited);

/// <summary>
/// Label scans across every account, for the admin area and the feedback CLI (SPEC 11, 12): the model's reading, the
/// values offered and what was saved, to find misreads. Reads ignore the user filters, so this is only reachable from
/// the CLI and controllers under the admin policy (AdminEvalTests checks it).
/// </summary>
public class LabelScanReport(AppDbContext db, AdminAuditLog audit)
{
    private IQueryable<LabelScan> Scans => db.LabelScans.IgnoreQueryFilters().AsNoTracking();

    public async Task<List<LabelScanRow>> ListAsync(DateTimeOffset since, bool editedOnly, bool failedOnly, int limit, CancellationToken ct)
    {
        var names = db.Profiles.IgnoreQueryFilters();
        var photos = db.LabelScanPhotos.IgnoreQueryFilters();
        return await Scans
            .Where(s => s.CreatedAt >= since)
            .Where(s => !editedOnly || s.Edited)
            .Where(s => !failedOnly || s.Error != null)
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
            .Take(limit)
            .Select(s => new LabelScanRow(s.Id, s.CreatedAt, names.Where(p => p.UserId == s.UserId).Select(p => p.DisplayName).FirstOrDefault() ?? "?",
                s.Model, s.LatencyMs, s.Error, s.FoodItemId != null, s.Edited, photos.Any(p => p.LabelScanId == s.Id)))
            .ToListAsync(ct);
    }

    public async Task<LabelScanDetail?> GetAsync(int id, CancellationToken ct)
    {
        var scan = await Scans.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (scan is null)
        {
            return null;
        }

        var reporter = await db.Profiles.IgnoreQueryFilters().Where(p => p.UserId == scan.UserId).Select(p => p.DisplayName).FirstOrDefaultAsync(ct) ?? "?";
        var hasPhoto = await db.LabelScanPhotos.IgnoreQueryFilters().AnyAsync(p => p.LabelScanId == id, ct);
        return new LabelScanDetail(
            new LabelScanRow(scan.Id, scan.CreatedAt, reporter, scan.Model, scan.LatencyMs, scan.Error, scan.FoodItemId != null, scan.Edited, hasPhoto),
            scan.Reading, scan.Values, scan.Saved, scan.FoodItemId);
    }

    /// <summary>The photo while it is kept (30 days). Every download is audited: it is a picture from someone's kitchen.</summary>
    public async Task<LabelScanPhoto?> PhotoAsync(int id, CancellationToken ct)
    {
        var photo = await db.LabelScanPhotos.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(p => p.LabelScanId == id, ct);
        if (photo is not null)
        {
            await audit.WriteAsync(AuditActions.LabelPhotoDownload, $"label scan #{id}", null, ct);
        }

        return photo;
    }

    public async Task<LabelScanCounts> CountsAsync(DateTimeOffset since, CancellationToken ct)
    {
        var scans = Scans.Where(s => s.CreatedAt >= since);
        return new LabelScanCounts(await scans.CountAsync(ct), await scans.CountAsync(s => s.Error != null, ct), await scans.CountAsync(s => s.Edited, ct));
    }
}
