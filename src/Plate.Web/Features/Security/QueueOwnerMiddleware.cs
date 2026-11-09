using System.Security.Claims;

namespace Plate.Web.Features.Security;

/// <summary>A stale tab must never replay its owner's pending changes with another account's cookie.</summary>
public sealed class QueueOwnerMiddleware(RequestDelegate next)
{
    public const string OwnerHeader = "X-Gym-User";
    public const string QueueHeader = "X-Gym-Queue";

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Headers.ContainsKey(QueueHeader)
            && context.User.Identity?.IsAuthenticated == true
            && string.IsNullOrWhiteSpace(context.Request.Headers[OwnerHeader].ToString()))
        {
            // Older tabs send the queue marker without an owner and discard most 4xx responses.
            // Refuse the write without making those clients delete their only pending copy.
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers["Retry-After"] = "60";
            return Task.CompletedTask;
        }

        if (context.Request.Headers.ContainsKey(QueueHeader)
            && context.User.Identity?.IsAuthenticated == true
            && (context.Request.Headers[OwnerHeader].Count != 1
                || context.Request.Headers[OwnerHeader].ToString() != context.User.FindFirstValue(ClaimTypes.NameIdentifier)))
        {
            // Before MVC/antiforgery: a stale token otherwise looks like a permanent validation error
            // to the queue and causes it to throw away the original account's work.
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            context.Response.Headers["X-Gym-Account-Mismatch"] = "1";
            return Task.CompletedTask;
        }

        return next(context);
    }
}
