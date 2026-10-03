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
    public const string ModelSettingName = "OpenRouter:Model";
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

    public async Task ClearKeyAsync(CancellationToken ct) =>
        await db.AppSettings.Where(s => s.Name == KeySettingName).ExecuteDeleteAsync(ct);

    public async Task<string> GetModelAsync(CancellationToken ct) =>
        await StoredAsync(ModelSettingName, ct)
        ?? (string.IsNullOrWhiteSpace(options.Value.Model) ? OpenRouterOptions.DefaultModel : options.Value.Model.Trim());

    /// <summary>Only models in OpenRouter's current catalog that can return structured JSON are accepted.</summary>
    public async Task SetModelAsync(string id, CancellationToken ct)
    {
        var models = await catalog.ListAsync(ct)
            ?? throw new Features.ValidationException("OpenRouter's model list can't be loaded right now. Try again later.");
        if (models.All(m => m.Id != id))
        {
            throw new Features.ValidationException("That model can't parse food.");
        }

        await SaveAsync(ModelSettingName, id, ct);
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
    }

    private Task<string?> StoredAsync(string name, CancellationToken ct) =>
        db.AppSettings.AsNoTracking().Where(s => s.Name == name).Select(s => s.Value).SingleOrDefaultAsync(ct);

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
            ? await services.GetRequiredService<OpenRouterFoodParser>().ParseAsync(text, key, await settings.GetModelAsync(ct), ct)
            : await new FakeFoodParser().ParseAsync(text, ct);
}
