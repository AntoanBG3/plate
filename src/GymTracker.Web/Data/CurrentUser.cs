using System.Security.Claims;

namespace GymTracker.Web.Data;

/// <summary>The user whose data the DbContext's query filters expose. Null means no user data is visible.</summary>
public interface ICurrentUser
{
    string? UserId { get; }
}

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public string? UserId => accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
}

/// <summary>For the CLI and tests, where there is no HTTP request.</summary>
public sealed class FixedCurrentUser(string? userId) : ICurrentUser
{
    public string? UserId { get; } = userId;
}
