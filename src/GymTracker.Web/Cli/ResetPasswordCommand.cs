using GymTracker.Web.Data;
using GymTracker.Web.Features.Admin;
using Microsoft.AspNetCore.Identity;

namespace GymTracker.Web.Cli;

/// <summary>
/// <c>dotnet GymTracker.Web.dll reset-password --email ...</c>: the only way back in after a forgotten password, since
/// the app sends no email (SPEC 9). Clears any lockout and signs the account out everywhere. Passkeys are kept.
/// </summary>
public static class ResetPasswordCommand
{
    public const string Name = "reset-password";

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter? output = null, TextReader? input = null)
    {
        var o = output ?? Console.Out;
        if (CliArgs.Option(args, "--email") is not { } email)
        {
            Console.Error.WriteLine("Usage: reset-password --email <email>");
            return 2;
        }

        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        if (await users.FindByEmailAsync(email) is not { } user)
        {
            Console.Error.WriteLine($"No account with the email {email}.");
            return 1;
        }

        var password = CreateUserCommand.ReadPassword("New password: ", input);
        if (input is null && !Console.IsInputRedirected && CreateUserCommand.ReadPassword("Repeat password: ") != password)
        {
            Console.Error.WriteLine("Passwords do not match.");
            return 1;
        }

        // Resetting through a token validates the password and updates the security stamp, which signs out every session.
        var result = await users.ResetPasswordAsync(user, await users.GeneratePasswordResetTokenAsync(user), password);
        if (!result.Succeeded)
        {
            Console.Error.WriteLine(string.Join(" ", result.Errors.Select(e => e.Description)));
            return 1;
        }

        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        await scope.ServiceProvider.GetRequiredService<AdminAuditLog>().WriteAsync(AuditActions.PasswordReset, user.Email, null, CancellationToken.None);
        o.WriteLine($"Password reset for {user.Email}. Its other sessions sign out within 5 minutes.");
        return 0;
    }
}
