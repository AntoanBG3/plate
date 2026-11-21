using System.Security.Claims;
using Plate.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Features.Admin;

/// <summary>
/// Writes and reads the admin audit trail (SPEC 12). In a web request it records the signed-in account and the client
/// address; without one (the CLI) it records Via = Cli.
/// </summary>
public class AdminAuditLog(AppDbContext db, IHttpContextAccessor http, TimeProvider time)
{
    public async Task WriteAsync(string action, string? target, string? detail, CancellationToken ct)
    {
        var context = http.HttpContext;
        db.AdminAudit.Add(new AdminAudit
        {
            At = time.GetUtcNow(),
            UserId = context?.User.FindFirstValue(ClaimTypes.NameIdentifier),
            Via = context is null ? AdminVia.Cli : AdminVia.Web,
            Action = action,
            Target = target,
            Ip = context?.Connection.RemoteIpAddress?.ToString(),
            Detail = detail is { Length: > AdminAudit.MaxDetailLength } ? detail[..(AdminAudit.MaxDetailLength - 1)] + "…" : detail,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>"the CLI" or "the web from 1.2.3.4", for alert texts.</summary>
    public string Origin => http.HttpContext is { } context ? $"the web from {context.Connection.RemoteIpAddress}" : "the CLI";

    public Task<List<AdminAudit>> RecentAsync(DateTimeOffset since, int limit, CancellationToken ct) =>
        db.AdminAudit.AsNoTracking().Where(a => a.At >= since).OrderByDescending(a => a.At).Take(limit).ToListAsync(ct);
}

/// <summary>The audit action names, kept short and stable for the CLI's output.</summary>
public static class AuditActions
{
    public const string Unlock = "unlock";
    public const string UnlockFailed = "unlock-failed";
    public const string Lock = "lock";
    public const string KeySet = "key-set";
    public const string KeyTest = "key-test";
    public const string KeyRemove = "key-remove";
    public const string ModelSet = "model-set";
    public const string EffortSet = "effort-set";
    public const string Status = "status";
    public const string ChangelogAdd = "changelog-add";
    public const string ChangelogRemove = "changelog-remove";
    public const string Eval = "eval";
    public const string ExportCases = "export-cases";
    public const string RoleGrant = "role-grant";
    public const string RoleRevoke = "role-revoke";
    public const string InviteCreate = "invite-create";
    public const string InviteRevoke = "invite-revoke";
    public const string InviteRedeem = "invite-redeem";
    public const string PasswordReset = "password-reset";
    public const string UserDelete = "user-delete";
    public const string LabelPhotoDownload = "label-photo-download";
}
