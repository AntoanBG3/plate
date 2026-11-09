using Plate.Core.Text;
using Plate.Web.Features.Feedback;

namespace Plate.Web.Features.Admin;

/// <summary>An evaluation started from the admin area: its progress, then its report or why it failed.</summary>
public sealed record EvalJob(EvalRequest Request, DateTimeOffset StartedAt, int Done, int Total, EvalReport? Report, string? Error, DateTimeOffset? FinishedAt)
{
    public bool Running => FinishedAt is null;
}

/// <summary>
/// Runs the parse evaluation in the background for the admin area (SPEC 12): one at a time, in its own scope, with
/// the last result kept in memory until the next run or a restart. The CLI's <c>feedback eval</c> runs it directly.
/// </summary>
public sealed class EvalJobs(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, TimeProvider time, ILogger<EvalJobs> log)
{
    public const int MaxLimit = 20;

    private readonly Lock gate = new();
    private EvalJob? current;

    public EvalJob? Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    /// <summary>Starts a run; false when one is already running.</summary>
    public bool TryStart(EvalRequest request)
    {
        request = request with { Limit = Math.Clamp(request.Limit, 1, MaxLimit) };
        lock (gate)
        {
            if (current is { Running: true })
            {
                return false;
            }

            current = new EvalJob(request, time.GetUtcNow(), 0, 0, null, null, null);
        }

        _ = Task.Run(() => RunAsync(request, lifetime.ApplicationStopping));
        return true;
    }

    private async Task RunAsync(EvalRequest request, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var report = await scope.ServiceProvider.GetRequiredService<IParseEvaluator>().RunAsync(request,
                (_, done, total) => Update(j => j with { Done = done, Total = total }), ct);
            Update(j => j with { Report = report, Done = report.Cases.Count, Total = report.Cases.Count, FinishedAt = time.GetUtcNow() });
        }
        catch (Exception ex)
        {
            if (ex is not ValidationException)
            {
                log.LogWarning(ex, "Admin evaluation failed.");
            }

            var message = ex is ValidationException v ? v.Text.Format : ex is OperationCanceledException ? "Stopped by a restart." : ex.Message;
            Update(j => j with { Error = message, FinishedAt = time.GetUtcNow() });
        }
    }

    private void Update(Func<EvalJob, EvalJob> change)
    {
        lock (gate)
        {
            if (current is not null)
            {
                current = change(current);
            }
        }
    }
}
