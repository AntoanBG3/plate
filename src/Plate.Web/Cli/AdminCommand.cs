using System.Globalization;
using Plate.Web.Features.Admin;

namespace Plate.Web.Cli;

/// <summary><c>dotnet Plate.Web.dll admin audit [--since 30d] [--json]</c>: the admin audit trail (SPEC 12).</summary>
public static class AdminCommand
{
    public const string Name = "admin";

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter? output = null)
    {
        var o = output ?? Console.Out;
        var rest = args.Skip(1).ToArray();
        if (rest is not ["audit", ..])
        {
            Console.Error.WriteLine("Usage: admin audit [--since 30d|<date>] [--json]");
            return 2;
        }

        DateTimeOffset since;
        try
        {
            since = Features.Feedback.TimeWindow.Since(CliArgs.Option(rest, "--since"), TimeSpan.FromDays(30), DateTimeOffset.UtcNow)!.Value;
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        using var scope = services.CreateScope();
        var entries = await scope.ServiceProvider.GetRequiredService<AdminAuditLog>().RecentAsync(since, 500, CancellationToken.None);
        if (rest.Contains("--json"))
        {
            FeedbackCommand.WriteJson(o, entries);
            return 0;
        }

        foreach (var e in entries)
        {
            o.WriteLine(string.Join("  ", new[]
            {
                e.At.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "Z",
                e.Via.ToString().ToLowerInvariant(),
                e.Action,
                e.Target,
                e.Detail,
                e.Ip,
            }.Where(s => !string.IsNullOrEmpty(s))));
        }

        o.WriteLine($"{entries.Count} entries.");
        return 0;
    }
}
