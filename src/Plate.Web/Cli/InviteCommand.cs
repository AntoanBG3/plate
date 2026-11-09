using System.Globalization;
using Plate.Core.Domain;
using Plate.Web.Features;
using Plate.Web.Features.Admin;

namespace Plate.Web.Cli;

/// <summary>
/// <c>dotnet Plate.Web.dll invite create|list|revoke</c>: single-use links to create an account (SPEC 9 and 12).
/// The link is printed once; only its hash is stored.
/// </summary>
public static class InviteCommand
{
    public const string Name = "invite";

    private const string Usage = """
        Usage: invite <command>
          create [--note <text>] [--expires 7d]   prints the link, once
          list [--all]                            open invites, or all of them
          revoke <id>
        """;

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter? output = null)
    {
        var o = output ?? Console.Out;
        var rest = args.Skip(1).ToArray();
        using var scope = services.CreateScope();
        var invites = scope.ServiceProvider.GetRequiredService<InviteAdmin>();
        var ct = CancellationToken.None;

        try
        {
            switch (rest)
            {
                case ["create", ..]:
                    if (Days(CliArgs.Option(rest, "--expires")) is not { } days)
                    {
                        Console.Error.WriteLine("--expires takes a number of days, like 7d.");
                        return 2;
                    }

                    var (invite, token) = await invites.CreateAsync(null, CliArgs.Option(rest, "--note"), days, ct);
                    var domain = scope.ServiceProvider.GetRequiredService<IConfiguration>()["Passkeys:ServerDomain"] is { Length: > 0 } d ? d : "plate.peychev.uk";
                    o.WriteLine($"Invite {invite.Id}{(invite.Note is null ? "" : $" ({invite.Note})")}, valid until {Time(invite.ExpiresAt)}:");
                    o.WriteLine($"https://{domain}/join/{token}");
                    o.WriteLine("The link is shown only now. Send it yourself; the app sends no email.");
                    return 0;

                case ["list", ..]:
                    var rows = await invites.ListAsync(rest.Contains("--all"), ct);
                    foreach (var row in rows)
                    {
                        o.WriteLine(string.Join("  ", new[]
                        {
                            row.Invite.Id.ToString(CultureInfo.InvariantCulture),
                            row.Status.ToString().ToLowerInvariant(),
                            "made " + Time(row.Invite.CreatedAt),
                            row.Status == InviteStatus.Redeemed ? "used " + Time(row.Invite.RedeemedAt!.Value) : "until " + Time(row.Invite.ExpiresAt),
                            row.Invite.Note,
                            row.RedeemedByEmail,
                        }.Where(s => !string.IsNullOrEmpty(s))));
                    }

                    o.WriteLine($"{rows.Count} invites.");
                    return 0;

                case ["revoke", var idText, ..] when int.TryParse(idText, CultureInfo.InvariantCulture, out var id):
                    await invites.RevokeAsync(id, ct);
                    o.WriteLine($"Invite {id} revoked.");
                    return 0;

                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (ValidationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (NotFoundException)
        {
            Console.Error.WriteLine("No such invite.");
            return 1;
        }
    }

    /// <summary>"7d" or "7"; null when it isn't a whole number of days. The range is checked by InviteAdmin.</summary>
    internal static int? Days(string? text) =>
        text is null ? InviteRules.DefaultDays
        : int.TryParse(text.EndsWith('d') ? text[..^1] : text, NumberStyles.None, CultureInfo.InvariantCulture, out var days) ? days
        : null;

    private static string Time(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "Z";
}
