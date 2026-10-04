using GymTracker.Web.Data;
using GymTracker.Web.Features.Admin;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Cli;

/// <summary>
/// <c>dotnet GymTracker.Web.dll role grant|revoke dev --email ...</c> and <c>role list</c>: the only way to give an
/// account the admin area (SPEC 12). There is deliberately no web page for this.
/// </summary>
public static class RoleCommand
{
    public const string Name = "role";

    private const string Usage = """
        Usage: role <command>
          grant dev --email <email>
          revoke dev --email <email>
          list
        """;

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter? output = null)
    {
        var o = output ?? Console.Out;
        var rest = args.Skip(1).ToArray();
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var audit = scope.ServiceProvider.GetRequiredService<AdminAuditLog>();
        var ct = CancellationToken.None;

        if (rest is ["list", ..])
        {
            var devs = await users.GetUsersInRoleAsync(AdminPolicy.Role);
            foreach (var user in await users.Users.OrderBy(u => u.Email).ToListAsync(ct))
            {
                o.WriteLine($"{user.Email}{(devs.Any(d => d.Id == user.Id) ? "  " + AdminPolicy.Role : "")}");
            }

            return 0;
        }

        if (rest is not [("grant" or "revoke") and var verb, var role, ..] || !string.Equals(role, AdminPolicy.Role, StringComparison.OrdinalIgnoreCase)
            || CliArgs.Option(rest, "--email") is not { } email)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var account = await users.FindByEmailAsync(email);
        if (account is null)
        {
            Console.Error.WriteLine($"No account with the email {email}.");
            return 1;
        }

        if (!await roles.RoleExistsAsync(AdminPolicy.Role))
        {
            await roles.CreateAsync(new IdentityRole(AdminPolicy.Role));
        }

        var result = verb == "grant"
            ? await users.IsInRoleAsync(account, AdminPolicy.Role) ? IdentityResult.Success : await users.AddToRoleAsync(account, AdminPolicy.Role)
            : await users.IsInRoleAsync(account, AdminPolicy.Role) ? await users.RemoveFromRoleAsync(account, AdminPolicy.Role) : IdentityResult.Success;
        if (!result.Succeeded)
        {
            Console.Error.WriteLine(string.Join(" ", result.Errors.Select(e => e.Description)));
            return 1;
        }

        await audit.WriteAsync(verb == "grant" ? AuditActions.RoleGrant : AuditActions.RoleRevoke, account.Email, AdminPolicy.Role, ct);
        o.WriteLine(verb == "grant"
            ? $"{account.Email} has the {AdminPolicy.Role} role. A signed-in phone picks it up within 5 minutes, or at the next sign-in."
            : $"{account.Email} no longer has the {AdminPolicy.Role} role. The admin area is closed to it now.");
        return 0;
    }
}
