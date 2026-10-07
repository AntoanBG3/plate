using System.Text;
using GymTracker.Web.Features.Users;

namespace GymTracker.Web.Cli;

/// <summary>
/// <c>dotnet GymTracker.Web.dll create-user --email ... --name ...</c>. Prompts for the password
/// without echoing it; when stdin is redirected it reads the password from the first line instead.
/// </summary>
public static class CreateUserCommand
{
    public const string Name = "create-user";

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var email = Option(args, "--email");
        var name = Option(args, "--name");
        if (email is null || name is null)
        {
            Console.Error.WriteLine("Usage: create-user --email <email> --name <display name>");
            return 2;
        }

        var password = ReadPassword("Password: ");
        if (!Console.IsInputRedirected && ReadPassword("Repeat password: ") != password)
        {
            Console.Error.WriteLine("Passwords do not match.");
            return 1;
        }

        using var scope = services.CreateScope();
        var creator = scope.ServiceProvider.GetRequiredService<UserCreator>();
        var errors = await creator.CreateAsync(email, name, password, CancellationToken.None);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                Console.Error.WriteLine(error);
            }

            return 1;
        }

        Console.WriteLine($"Created {email}.");
        return 0;
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>Reads a password without echoing it; from <paramref name="input"/> (tests) or redirected stdin, one line.</summary>
    internal static string ReadPassword(string prompt, TextReader? input = null)
    {
        if (input is not null || Console.IsInputRedirected)
        {
            return (input ?? Console.In).ReadLine() ?? "";
        }

        Console.Write(prompt);
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
    }
}
