using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GymTracker.Web.Features.Diagnostics;

/// <summary>Database time and query count for the current request, filled in by <see cref="DbTimingInterceptor"/>.</summary>
public sealed class RequestDbStats
{
    public int Queries;
    public double Milliseconds;
}

/// <summary>Counts EF commands per request. Registered as a singleton; the per-request totals live in HttpContext.Items.</summary>
public sealed class DbTimingInterceptor(IHttpContextAccessor accessor) : DbCommandInterceptor
{
    private void Record(CommandExecutedEventData data)
    {
        if (accessor.HttpContext?.Items[typeof(RequestDbStats)] is RequestDbStats stats)
        {
            stats.Queries++;
            stats.Milliseconds += data.Duration.TotalMilliseconds;
        }
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Record(eventData);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        Record(eventData);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Record(eventData);
        return ValueTask.FromResult(result);
    }
}

/// <summary>Adds a Server-Timing header so page cost shows in the browser's network panel and in the nav benchmark.</summary>
public sealed class ServerTimingMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var start = Stopwatch.GetTimestamp();
        var stats = new RequestDbStats();
        context.Items[typeof(RequestDbStats)] = stats;
        context.Response.OnStarting(() =>
        {
            var app = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context.Response.Headers.Append("Server-Timing",
                FormattableString.Invariant($"app;dur={app:0.0}, db;dur={stats.Milliseconds:0.0};desc=\"{stats.Queries} queries\""));
            return Task.CompletedTask;
        });
        return next(context);
    }
}
