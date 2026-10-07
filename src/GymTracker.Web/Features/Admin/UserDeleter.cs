using GymTracker.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Admin;

/// <summary>
/// Deletes an account and everything it owns, for <c>delete-user</c> (SPEC 12). Shared rows (foods added from a barcode,
/// changelog entries) stay, audit rows keep the user id, and an invite it came from keeps only that it was used.
/// </summary>
public class UserDeleter(AppDbContext db, UserManager<AppUser> users, AdminAuditLog audit)
{
    public async Task DeleteAsync(AppUser user, CancellationToken ct)
    {
        if (await users.IsInRoleAsync(user, AdminPolicy.Role) && (await users.GetUsersInRoleAsync(AdminPolicy.Role)).Count == 1)
        {
            throw new ValidationException($"{user.Email} is the last {AdminPolicy.Role} account. Grant the role to another account first.");
        }

        var id = user.Id;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Sessions and templates first: their exercises reference Exercise with RESTRICT, so cascading from the user
        // row fails when a session or template uses one of the account's own custom exercises.
        await db.Sets.IgnoreQueryFilters().Where(s => s.SessionExercise.Session.UserId == id).ExecuteDeleteAsync(ct);
        await db.SessionExercises.IgnoreQueryFilters().Where(x => x.Session.UserId == id).ExecuteDeleteAsync(ct);
        await db.Sessions.IgnoreQueryFilters().Where(s => s.UserId == id).ExecuteDeleteAsync(ct);
        await db.TemplateExercises.IgnoreQueryFilters().Where(x => x.Template.UserId == id).ExecuteDeleteAsync(ct);
        await db.Templates.IgnoreQueryFilters().Where(t => t.UserId == id).ExecuteDeleteAsync(ct);

        // Error events have no foreign key; they stay, without the account.
        await db.ErrorEvents.Where(e => e.UserId == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.UserId, (string?)null), ct);

        // Everything else cascades from the user row.
        var result = await users.DeleteAsync(user);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }

        await transaction.CommitAsync(ct);
        await audit.WriteAsync(AuditActions.UserDelete, id, user.Email, ct);
    }
}
