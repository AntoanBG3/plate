using Plate.Web.Features.Profile;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Plate.Web.Features.Onboarding;

/// <summary>
/// Sends a signed-in user who hasn't finished or skipped the setup chat to /welcome on any page load (SPEC 6.4).
/// Only page GETs are redirected: never htmx partials, posts, or the account, admin, feedback and error pages.
/// </summary>
public class OnboardingRedirectMiddleware(RequestDelegate next)
{
    private static readonly string[] Exempt = ["/welcome", "/account", "/admin", "/feedback", "/error", "/csp-report", "/settings/export"];

    public async Task InvokeAsync(HttpContext context, ProfileService profiles)
    {
        if (NeedsCheck(context)
            && await profiles.FindAsync(context.RequestAborted) is { OnboardedAt: null })
        {
            var returnUrl = context.Request.Path + context.Request.QueryString;
            context.Response.Redirect("/welcome" + (returnUrl is "/" or "/today" ? "" : "?returnUrl=" + Uri.EscapeDataString(returnUrl)));
            return;
        }

        await next(context);
    }

    private static bool NeedsCheck(HttpContext context) =>
        HttpMethods.IsGet(context.Request.Method)
        && context.User.Identity?.IsAuthenticated == true
        && !context.Request.Headers.ContainsKey("HX-Request")
        && context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is not null
        && !Exempt.Any(p => context.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));
}
