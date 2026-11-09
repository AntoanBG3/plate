using Plate.Web.Data;
using Plate.Web.Features;
using Plate.Web.Features.Admin;
using Microsoft.AspNetCore.Identity;

namespace Plate.Web.Cli;

/// <summary>
/// <c>dotnet Plate.Web.dll delete-user --email ... [--yes]</c>: deletes an account and all its data (SPEC 12).
/// Asks for the email again unless --yes is given.
/// </summary>
public static class DeleteUserCommand
{
    public const string Name = "delete-user";

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter? output = null, TextReader? input = null)
    {
        var o = output ?? Console.Out;
        if (CliArgs.Option(args, "--email") is not { } email)
        {
            Console.Error.WriteLine("Usage: delete-user --email <email> [--yes]");
            return 2;
        }

        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        if (await users.FindByEmailAsync(email) is not { } user)
        {
            Console.Error.WriteLine($"No account with the email {email}.");
            return 1;
        }

        if (!args.Contains("--yes"))
        {
            o.Write($"This deletes {user.Email} and all its data. The nightly dumps keep it for 14 days. Type the email again to confirm: ");
            if (!string.Equals((input ?? Console.In).ReadLine()?.Trim(), user.Email, StringComparison.OrdinalIgnoreCase))
            {
                o.WriteLine();
                Console.Error.WriteLine("Not deleted.");
                return 1;
            }
        }

        try
        {
            await scope.ServiceProvider.GetRequiredService<UserDeleter>().DeleteAsync(user, CancellationToken.None);
        }
        catch (ValidationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        o.WriteLine($"Deleted {user.Email} and all its data.");
        return 0;
    }
}
