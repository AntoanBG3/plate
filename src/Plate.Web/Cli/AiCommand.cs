using Plate.Web.Features;
using Plate.Web.Features.Admin;
using Plate.Web.Features.Localization;
using Plate.Web.Integrations;

namespace Plate.Web.Cli;

/// <summary>
/// <c>dotnet Plate.Web.dll ai &lt;command&gt;</c>: the shared OpenRouter key and AI models, as in the admin area
/// (SPEC 12). The key is read from standard input, never from an argument, which would end up in shell history and
/// the process list.
/// </summary>
public static class AiCommand
{
    public const string Name = "ai";

    private const string Usage = """
        Usage: ai <command>
          show
          set-model parser|matcher|onboarding|weekly|label|estimator <model id>
          set-effort parser|onboarding|weekly|label|estimator <effort>|lowest
          set-key                         reads the key from standard input
          clear-key
          test [label]
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
                        var model = await settings.GetModelAsync(task, ct);
                        var info = AiTasks.HasEffort(task) && model != AiTasks.Off ? await settings.FindModelAsync(task, model, ct) : null;
                        var picked = await settings.GetEffortAsync(task, ct);
                        o.WriteLine($"{task}: {model}" + (info is { Efforts.Count: > 0 }
                            ? $", effort {info.EffortFor(picked)}{(picked is null ? " (lowest)" : "")} of {string.Join("/", info.Efforts)}"
                            : ""));
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

                case ["set-effort", var effortSlug, var effort, ..]:
                    if (AiTasks.FromSlug(effortSlug) is not { } et)
                    {
                        Console.Error.WriteLine(Usage);
                        return 2;
                    }

                    await ai.SetEffortAsync(et, effort, ct);
                    o.WriteLine($"{et} effort is now {await settings.GetEffortAsync(et, ct) ?? AiTasks.LowestEffort}.");
                    var effortError = await ai.TestAsync(et, ct);
                    o.WriteLine(effortError is null ? $"{et}: works." : $"{et}: {localizer[effortError]}");
                    return effortError is null ? 0 : 1;

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

                case ["test", "label"]:
                    var labelError = await ai.TestLabelAsync(ct);
                    o.WriteLine(labelError is null ? "Label: works or off." : $"Label: {localizer[labelError]}");
                    return labelError is null ? 0 : 1;

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
        var weekly = await ai.TestWeeklyAsync(ct);
        o.WriteLine(weekly is null ? "Weekly: works or off." : $"Weekly: {localizer[weekly]}");
        var label = await ai.TestLabelAsync(ct);
        o.WriteLine(label is null ? "Label: works or off." : $"Label: {localizer[label]}");
        var estimator = await ai.TestEstimatorAsync(ct);
        o.WriteLine(estimator is null ? "Estimator: works or off." : $"Estimator: {localizer[estimator]}");
        return parser is null && matcher is null && onboarding is null && weekly is null && label is null && estimator is null ? 0 : 1;
    }
}
