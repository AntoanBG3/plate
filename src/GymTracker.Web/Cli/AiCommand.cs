using GymTracker.Web.Features;
using GymTracker.Web.Features.Admin;
using GymTracker.Web.Features.Localization;
using GymTracker.Web.Integrations;

namespace GymTracker.Web.Cli;

/// <summary>
/// <c>dotnet GymTracker.Web.dll ai &lt;command&gt;</c>: the shared OpenRouter key and AI models, as in the admin area
/// (SPEC 12). The key is read from standard input, never from an argument, which would end up in shell history and
/// the process list.
/// </summary>
public static class AiCommand
{
    public const string Name = "ai";

    private const string Usage = """
        Usage: ai <command>
          show
          set-model parser|matcher|onboarding <model id>
          set-key                         reads the key from standard input
          clear-key
          test
        """;

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter? output = null, TextReader? input = null)
    {
        var o = output ?? Console.Out;
        var rest = args.Skip(1).ToArray();
        using var scope = services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<OpenRouterSettings>();
        var ai = scope.ServiceProvider.GetRequiredService<AiAdmin>();
        var localizer = scope.ServiceProvider.GetRequiredService<Localizer>();
        var ct = CancellationToken.None;
        try
        {
            switch (rest)
            {
                case ["show", ..]:
                    var key = await settings.KeyStatusAsync(ct);
                    o.WriteLine(key.Source switch
                    {
                        KeySource.None => "Key: none (food parsing uses the simple built-in parser)",
                        KeySource.Environment => $"Key: from OPENROUTER_API_KEY, ending {key.Last4}",
                        _ => $"Key: saved in the database, ending {key.Last4}",
                    });
                    foreach (var task in Enum.GetValues<AiTask>())
                    {
                        o.WriteLine($"{task}: {await settings.GetModelAsync(task, ct)}");
                    }

                    return 0;

                case ["set-model", var slug, var model, ..]:
                    if (AiTasks.FromSlug(slug) is not { } t)
                    {
                        Console.Error.WriteLine(Usage);
                        return 2;
                    }

                    await ai.SetModelAsync(t, model, ct);
                    o.WriteLine($"{t} model is now {model}.");
                    return await TestAsync(ai, localizer, o, ct);

                case ["set-key", ..]:
                    var newKey = input is not null ? await input.ReadLineAsync(ct) ?? "" : CreateUserCommand.ReadPassword("OpenRouter key: ");
                    await ai.SetKeyAsync(newKey, ct);
                    o.WriteLine($"Key saved, ending {(await settings.KeyStatusAsync(ct)).Last4}.");
                    return await TestAsync(ai, localizer, o, ct);

                case ["clear-key", ..]:
                    await ai.ClearKeyAsync(ct);
                    var after = await settings.KeyStatusAsync(ct);
                    o.WriteLine(after.Source == KeySource.Environment
                        ? $"Key removed from the database; OPENROUTER_API_KEY (ending {after.Last4}) is used now."
                        : "Key removed.");
                    return 0;

                case ["test", ..]:
                    return await TestAsync(ai, localizer, o, ct);

                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (ValidationException ex)
        {
            Console.Error.WriteLine(localizer[ex.Text]);
            return 1;
        }
    }

    private static async Task<int> TestAsync(AiAdmin ai, Localizer localizer, TextWriter o, CancellationToken ct)
    {
        var parser = await ai.TestParserAsync(ct);
        o.WriteLine(parser is null ? "Parser: works." : $"Parser: {localizer[parser]}");
        var matcher = await ai.TestMatcherAsync(ct);
        o.WriteLine(matcher is null ? "Matcher: works or off." : $"Matcher: {localizer[matcher]}");
        var onboarding = await ai.TestOnboardingAsync(ct);
        o.WriteLine(onboarding is null ? "Onboarding: works or off." : $"Onboarding: {localizer[onboarding]}");
        return parser is null && matcher is null && onboarding is null ? 0 : 1;
    }
}
