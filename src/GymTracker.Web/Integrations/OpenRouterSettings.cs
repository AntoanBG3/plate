using System.Security.Cryptography;
using GymTracker.Web.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GymTracker.Web.Integrations;

public enum KeySource
{
    None,
    Environment,
    Settings,
}

/// <param name="Last4">The last four characters, so a user can tell which key is in use without seeing it.</param>
public record OpenRouterKeyStatus(KeySource Source, string? Last4);

/// <summary>
/// The OpenRouter API key and model, shared by both accounts. A key set in Settings (encrypted with Data Protection)
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

    private readonly IDataProtector protector = protection.CreateProtector("GymTracker.Settings.OpenRouterApiKey");

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
    /// The model picked for a task in Settings, else its default: the parser's from configuration, the setup chat
    /// the parser's model (any chat model with structured output will do), the matcher off.
    /// </summary>
    public async Task<string> GetModelAsync(AiTask task, CancellationToken ct) =>
        await StoredAsync(AiTasks.SettingName(task), ct) ?? task switch
        {
            AiTask.Parser => string.IsNullOrWhiteSpace(options.Value.Model) ? OpenRouterOptions.DefaultModel : options.Value.Model.Trim(),
            AiTask.Onboarding or AiTask.Weekly => await GetModelAsync(AiTask.Parser, ct),
            _ => AiTasks.Off,
        };

    /// <summary>The models a task can use; null when OpenRouter's list can't be loaded.</summary>
    public async Task<IReadOnlyList<OpenRouterModel>?> ListModelsAsync(AiTask task, CancellationToken ct) => task switch
    {
        AiTask.Parser => await catalog.ListAsync(ct),
        AiTask.Onboarding => await catalog.ListAsync(ct) is { } models ? [AiTasks.OnboardingOff, .. models] : null,
        AiTask.Weekly => await catalog.ListAsync(ct) is { } weekly ? [AiTasks.WeeklyOff, .. weekly] : null,
        _ => AiTasks.MatcherModels,
    };

    public async Task<OpenRouterModel?> FindModelAsync(AiTask task, string id, CancellationToken ct) =>
        (await ListModelsAsync(task, ct))?.FirstOrDefault(m => m.Id == id);

    /// <summary>Only listed models are accepted: for the parser, catalog models that can return structured JSON.</summary>
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
public class SwitchingFoodParser(OpenRouterSettings settings, IServiceProvider services) : GymTracker.Core.Nutrition.IFoodParser
{
    public async Task<GymTracker.Core.Nutrition.FoodParseResult> ParseAsync(string text, CancellationToken ct) =>
        await settings.GetKeyAsync(ct) is { } key
            ? await services.GetRequiredService<OpenRouterFoodParser>().ParseAsync(text, key, await settings.GetModelAsync(AiTask.Parser, ct), ct)
            : await new FakeFoodParser().ParseAsync(text, ct);
}

/// <summary>Asks the matcher model picked in Settings when there is a key; otherwise no decision, so the ranking decides.</summary>
public class SwitchingMatchDecider(OpenRouterSettings settings, IServiceProvider services) : GymTracker.Core.Nutrition.IFoodMatchDecider
{
    public async Task<GymTracker.Core.Nutrition.MatchDecisions?> DecideAsync(string text, IReadOnlyList<GymTracker.Core.Nutrition.MatchQuestion> questions, CancellationToken ct)
    {
        var model = await settings.GetModelAsync(AiTask.Matcher, ct);
        if (model == AiTasks.Off || await settings.GetKeyAsync(ct) is not { } key)
        {
            return null;
        }

        return await services.GetRequiredService<JevFoodMatchDecider>().DecideAsync(text, questions, key, model, ct);
    }
}
