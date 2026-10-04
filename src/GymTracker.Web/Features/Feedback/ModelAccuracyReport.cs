using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Data;
using GymTracker.Web.Integrations;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Feedback;

/// <summary>
/// Correction and thumbs-down rates per model over the last 90 days (SPEC 11). Pooled across both accounts, like
/// the model setting itself; only counts leave this class, never anyone's entries.
/// </summary>
public class ModelAccuracyReport(AppDbContext db, TimeProvider time)
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(90);

    public async Task<Dictionary<string, ModelStats>> ForAsync(AiTask task, CancellationToken ct)
    {
        var since = time.GetUtcNow() - Window;
        var feedback = db.Feedback.IgnoreQueryFilters();
        var runs = await db.ParseRuns.IgnoreQueryFilters()
            .Where(r => r.CreatedAt >= since && r.ParserError == null)
            .Select(r => new
            {
                r.AiModel,
                r.MatchModel,
                Confirmed = r.ConfirmedAt != null,
                Corrected = r.Corrections != ParseCorrection.None,
                RatedDown = feedback.Any(f => f.ParseRunId == r.Id && f.Rating == FeedbackRating.Down),
            })
            .ToListAsync(ct);
        return ModelAccuracy.Summarize(runs.Select(r => new RunOutcome(
            task == AiTask.Parser ? r.AiModel ?? "?" : r.MatchModel ?? AiTasks.Off, r.Confirmed, r.Corrected, r.RatedDown)));
    }
}
