using GymTracker.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Identity;

namespace GymTracker.Web.Features.Admin;

/// <summary>
/// Who may use the admin area (SPEC 12): a signed-in account with the Dev role, through Cloudflare Access, and for
/// everything but the unlock page itself, with a fresh passkey unlock. Anything short of the role is a 404.
/// </summary>
public static class AdminPolicy
{
    /// <summary>Every admin page: role, Access and an unlock.</summary>
    public const string Name = "Admin";

    /// <summary>The unlock page: role and Access.</summary>
    public const string Unlock = "AdminUnlock";

    public const string Role = "Dev";

    public static void Add(AuthorizationOptions o)
    {
        o.AddPolicy(Name, p => p.RequireAuthenticatedUser().AddRequirements(new AdminRequirement(Unlocked: true)));
        o.AddPolicy(Unlock, p => p.RequireAuthenticatedUser().AddRequirements(new AdminRequirement(Unlocked: false)));
    }
}

public sealed record AdminRequirement(bool Unlocked) : IAuthorizationRequirement;

public sealed class AdminHandler(UserManager<AppUser> users, CloudflareAccess access, IHttpContextAccessor accessor, TimeProvider time)
    : AuthorizationHandler<AdminRequirement>
{
    public const string Locked = "locked";
    private const string Hidden = "hidden";

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        // Not signed in: the policy's authenticated-user requirement fails too, and the user is sent to log in.
        if (context.User.Identity?.IsAuthenticated != true || accessor.HttpContext is not { } http)
        {
            return;
        }

        // The role claim is checked first so other accounts cost no query; the database then has the final say, since
        // the claim can be up to five minutes old after a revoke.
        if (!context.User.IsInRole(AdminPolicy.Role) || await users.GetUserAsync(context.User) is not { } user || !await users.IsInRoleAsync(user, AdminPolicy.Role))
        {
            context.Fail(new AuthorizationFailureReason(this, Hidden));
            return;
        }

        if (!await access.VerifyAsync(http.Request, user.Email, http.RequestAborted))
        {
            context.Fail(new AuthorizationFailureReason(this, Hidden));
            return;
        }

        if (requirement.Unlocked && await AdminUnlock.ExpiresAsync(http, user.Id, await users.GetSecurityStampAsync(user), time.GetUtcNow()) is null)
        {
            context.Fail(new AuthorizationFailureReason(this, Locked));
            return;
        }

        context.Succeed(requirement);
    }
}

/// <summary>Turns an admin policy's "forbidden" into a 404, or into the unlock page for a Dev account that hasn't unlocked.</summary>
public sealed class AdminResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (!result.Forbidden || !policy.Requirements.OfType<AdminRequirement>().Any())
        {
            await fallback.HandleAsync(next, context, policy, result);
            return;
        }

        if (result.AuthorizationFailure?.FailureReasons.Any(r => r.Message == AdminHandler.Locked) == true)
        {
            var unlock = "/admin/unlock?returnUrl=" + Uri.EscapeDataString(HttpMethods.IsGet(context.Request.Method)
                ? context.Request.Path + context.Request.QueryString
                : "/admin");
            if (context.Request.Headers.ContainsKey("HX-Request"))
            {
                context.Response.Headers["HX-Redirect"] = unlock;
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            }
            else
            {
                context.Response.Redirect(unlock);
            }

            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }
}
