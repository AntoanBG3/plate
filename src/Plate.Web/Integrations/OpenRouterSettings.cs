using System.Security.Cryptography;
using Plate.Web.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Plate.Web.Integrations;

public enum KeySource
{
    None,
    Environment,
    Settings,
}

/// <param name="Last4">The last four characters, so a user can tell which key is in use without seeing it.</param>
public record OpenRouterKeyStatus(KeySource Source, string? Last4);

/// <summary>
/// The OpenRouter API key and model, shared by all accounts. A key set in Settings (encrypted with Data Protection)
/// wins over OPENROUTER_API_KEY from the environment; a model picked in Settings wins over OPENROUTER_MODEL. The key
/// itself is never shown or logged.
/// </summary>
public class OpenRouterSettings(
    AppDbContext db,
    ICurrentUser currentUser,
    IDataProtectionProvider protection,
    IOptions<OpenRouterOptions> options,
    OpenRouterModels catalog,
    TimeProvider time,
    ILogger<OpenRouterSettings> log)
{
    public const string KeySettingName = "OpenRouter:ApiKey";
    public const int MaxLength = 200;

    private readonly IDataProtector protector = protection.CreateProtector("Plate.Settings.OpenRouterApiKey");

    public async Task<string?> GetKeyAsync(CancellationToken ct) => (await ResolveKeyAsync(ct)).Key;

    public async Task<OpenRouterKeyStatus> KeyStatusAsync(CancellationToken ct)
    {
        var (key, source) = await ResolveKeyAsync(ct);
        return new OpenRouterKeyStatus(source, key is { Length: >= 8 } ? key[^4..] : null);
    }

    public async Task SetKeyAsync(string key, CancellationToken ct)
    {
        key = key.Trim();
        if (key.Length is < 20 or > MaxLength || key.Any(char.IsWhiteSpace))
        {
            throw new Features.ValidationException("That doesn't look like an API key.");
        }

        await SaveAsync(KeySettingName, protector.Protect(key), ct);
    }

    public async Task ClearKeyAsync(CancellationToken ct)
    {
        await db.AppSettings.Where(s => s.Name == KeySettingName).ExecuteDeleteAsync(ct);
        values = null;
    }

    /// <summary>
    /// The model picked for a task in Settings, else its default: the parser's from configuration, the setup chat,
    /// weekly summary and estimator the parser's model (any chat model with structured output will do), the matcher off.
    /// </summary>
    public async Task<string> GetModelAsync(AiTask task, CancellationToken ct) =>
        await StoredAsync(AiTasks.SettingName(task), ct) ?? task switch
        {
            AiTask.Parser => string.IsNullOrWhiteSpace(options.Value.Model) ? OpenRouterOptions.DefaultModel : options.Value.Model.Trim(),
            AiTask.Onboarding or AiTask.Weekly or AiTask.Estimator => await GetModelAsync(AiTask.Parser, ct),
            AiTask.Label => AiTasks.DefaultLabelModel,
            _ => AiTasks.Off,
        };

    /// <summary>
    /// The reasoning effort picked for a chat task, or null for the model's lowest, the default (SPEC 12). The food
    /// namer uses the parser's model, so it takes the parser's effort too.
    /// </summary>
    public async Task<string?> GetEffortAsync(AiTask task, CancellationToken ct) =>
        AiTasks.HasEffort(task) ? await StoredAsync(AiTasks.EffortSettingName(task), ct) : null;

    /// <summary>
    /// Picks the effort for a task's current model: one of the efforts the model lists, or "lowest" for the default.
    /// Changing the model later keeps the pick, which is sent only while the model accepts it.
    /// </summary>
    public async Task SetEffortAsync(AiTask task, string effort, CancellationToken ct)
    {
        effort = effort.Trim().ToLowerInvariant();
        if (!AiTasks.HasEffort(task))
        {
            throw new Features.ValidationException("The food matcher has no effort setting.");
        }

        if (effort is "" or AiTasks.LowestEffort)
        {
            await db.AppSettings.Where(s => s.Name == AiTasks.EffortSettingName(task)).ExecuteDeleteAsync(ct);
            values = null;
            return;
        }

        var model = await FindModelAsync(task, await GetModelAsync(task, ct), ct)
            ?? throw new Features.ValidationException("OpenRouter's model list can't be loaded right now. Try again later.");
        if (!(model.Efforts ?? []).Contains(effort))
        {
            throw new Features.ValidationException("That model doesn't take that effort.");
        }

        await SaveAsync(AiTasks.EffortSettingName(task), effort, ct);
    }

    /// <summary>The models a task can use; null when OpenRouter's list can't be loaded.</summary>
    public async Task<IReadOnlyList<OpenRouterModel>?> ListModelsAsync(AiTask task, CancellationToken ct) => task switch
    {
        AiTask.Parser => await catalog.ListAsync(ct),
        AiTask.Onboarding => await catalog.ListAsync(ct) is { } models ? [AiTasks.OnboardingOff, .. models] : null,
        AiTask.Weekly => await catalog.ListAsync(ct) is { } weekly ? [AiTasks.WeeklyOff, .. weekly] : null,
        AiTask.Label => await catalog.ListAsync(ct) is { } vision ? [AiTasks.LabelOff, .. vision.Where(m => m.AcceptsImages)] : null,
        AiTask.Estimator => await catalog.ListAsync(ct) is { } estimators ? [AiTasks.EstimatorOff, .. estimators] : null,
        _ => await catalog.ListDecisionsAsync(ct) is { } decisions ? [AiTasks.MatcherOff, .. decisions] : null,
    };

    /// <summary>
    /// What a task's picker shows: every current decision model for the matcher, and for the chat tasks the
    /// most used recent models (<see cref="AiTasks.ForPicker"/>). Any listed model can still be set from the CLI.
    /// </summary>
    public async Task<IReadOnlyList<OpenRouterModel>?> PickerModelsAsync(AiTask task, CancellationToken ct) =>
        await ListModelsAsync(task, ct) is { } models
            ? AiTasks.ForPicker(models, await GetModelAsync(task, ct), time.GetUtcNow(), task == AiTask.Matcher ? null : AiTasks.PickerSize)
            : null;

    public async Task<OpenRouterModel?> FindModelAsync(AiTask task, string id, CancellationToken ct) =>
        (await ListModelsAsync(task, ct))?.FirstOrDefault(m => m.Id == id);

    /// <summary>
    /// Only listed models are accepted: for the parser, catalog models that can return structured JSON; for the
    /// matcher, OpenRouter's decision models.
    /// </summary>
    public async Task SetModelAsync(AiTask task, string id, CancellationToken ct)
    {
        var models = await ListModelsAsync(task, ct)
            ?? throw new Features.ValidationException("OpenRouter's model list can't be loaded right now. Try again later.");
        if (models.All(m => m.Id != id))
        {
            throw new Features.ValidationException(task switch
            {
                AiTask.Parser => "That model can't parse food.",
                AiTask.Matcher => "That model can't match food.",
                AiTask.Weekly => "That model can't write the weekly summary.",
                AiTask.Label => "That model can't read labels.",
                AiTask.Estimator => "That model can't estimate food.",
                _ => "That model can't run the setup chat.",
            });
        }

        await SaveAsync(AiTasks.SettingName(task), id, ct);
    }

    private async Task SaveAsync(string name, string value, CancellationToken ct)
    {
        var setting = await db.AppSettings.SingleOrDefaultAsync(s => s.Name == name, ct);
        if (setting is null)
        {
            setting = new AppSetting { Name = name };
            db.AppSettings.Add(setting);
        }

        setting.Value = value;
        setting.UpdatedAt = time.GetUtcNow();
        setting.UpdatedByUserId = currentUser.UserId;
        await db.SaveChangesAsync(ct);
        values = null;
    }

    /// <summary>All settings in one query per request: a page asking for the key and several models costs one round trip.</summary>
    private Dictionary<string, string>? values;

    private async Task<string?> StoredAsync(string name, CancellationToken ct) =>
        (values ??= await db.AppSettings.AsNoTracking().ToDictionaryAsync(s => s.Name, s => s.Value, ct)).GetValueOrDefault(name);

    private async Task<(string? Key, KeySource Source)> ResolveKeyAsync(CancellationToken ct)
    {
        if (await StoredAsync(KeySettingName, ct) is { } stored)
        {
            try
            {
                return (protector.Unprotect(stored), KeySource.Settings);
            }
            catch (CryptographicException)
            {
                // The Data Protection keys were lost or replaced; the key has to be entered again.
                log.LogWarning("The OpenRouter key saved in Settings can no longer be decrypted; ignoring it");
            }
        }

        var fromEnvironment = options.Value.ApiKey?.Trim();
        return string.IsNullOrEmpty(fromEnvironment) ? (null, KeySource.None) : (fromEnvironment, KeySource.Environment);
    }
}

/// <summary>Uses OpenRouter when a key is available, otherwise the simple built-in parser.</summary>
public class SwitchingFoodParser(OpenRouterSettings settings, IServiceProvider services) : Plate.Core.Nutrition.IFoodParser
{
    public async Task<Plate.Core.Nutrition.FoodParseResult> ParseAsync(string text, CancellationToken ct) =>
        await settings.GetKeyAsync(ct) is { } key
            ? await services.GetRequiredService<OpenRouterFoodParser>().ParseAsync(text, key, await settings.GetModelAsync(AiTask.Parser, ct), ct, await settings.GetEffortAsync(AiTask.Parser, ct))
            : await new FakeFoodParser().ParseAsync(text, ct);
}

/// <summary>Names matched foods and their status with the parser's model when there is a key; otherwise no names, so the database names stand.</summary>
public class SwitchingFoodNamer(OpenRouterSettings settings, IServiceProvider services) : Plate.Core.Nutrition.IFoodNamer
{
    public async Task<IReadOnlyList<Plate.Core.Nutrition.FoodName?>?> NameAsync(IReadOnlyList<Plate.Core.Nutrition.NameRequest> items, CancellationToken ct) =>
        await settings.GetKeyAsync(ct) is { } key
            ? await services.GetRequiredService<OpenRouterFoodNamer>().NameAsync(items, key, await settings.GetModelAsync(AiTask.Parser, ct), ct, await settings.GetEffortAsync(AiTask.Parser, ct))
            : null;
}

/// <summary>Asks the matcher model picked in Settings when there is a key; otherwise no decision, so the ranking decides.</summary>
public class SwitchingMatchDecider(OpenRouterSettings settings, IServiceProvider services) : Plate.Core.Nutrition.IFoodMatchDecider
{
    public async Task<bool> IsEnabledAsync(CancellationToken ct) =>
        await settings.GetModelAsync(AiTask.Matcher, ct) != AiTasks.Off;

    public async Task<Plate.Core.Nutrition.MatchDecisions?> DecideAsync(string text, IReadOnlyList<Plate.Core.Nutrition.MatchQuestion> questions, CancellationToken ct)
    {
        var model = await settings.GetModelAsync(AiTask.Matcher, ct);
        if (model == AiTasks.Off || await settings.GetKeyAsync(ct) is not { } key)
        {
            return null;
        }

        return await services.GetRequiredService<JevFoodMatchDecider>().DecideAsync(text, questions, key, model, ct);
    }
}

/// <summary>
/// Estimates unmatched foods with the estimator model when there is a key and it isn't off; otherwise no answer, so the
/// parser's own guesses stand (SPEC 6.1 step 6).
/// </summary>
public class SwitchingFoodEstimator(OpenRouterSettings settings, IServiceProvider services) : Plate.Core.Nutrition.IFoodEstimator
{
    public async Task<Plate.Core.Nutrition.FoodEstimates?> EstimateAsync(string text, IReadOnlyList<Plate.Core.Nutrition.EstimateRequest> items, CancellationToken ct)
    {
        var model = await settings.GetModelAsync(AiTask.Estimator, ct);
        if (model == AiTasks.Off || await settings.GetKeyAsync(ct) is not { } key)
        {
            return null;
        }

        return await services.GetRequiredService<OpenRouterFoodEstimator>().EstimateAsync(text, items, key, model, ct, await settings.GetEffortAsync(AiTask.Estimator, ct));
    }
}
