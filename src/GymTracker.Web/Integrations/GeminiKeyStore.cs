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
public record GeminiKeyStatus(KeySource Source, string? Last4);

/// <summary>
/// The Gemini API key: one set in Settings (encrypted with Data Protection, shared by both accounts) wins over
/// GEMINI_API_KEY from the environment. The key itself is never shown or logged.
/// </summary>
public class GeminiKeyStore(AppDbContext db, ICurrentUser currentUser, IDataProtectionProvider protection, IOptions<GeminiOptions> options, TimeProvider time, ILogger<GeminiKeyStore> log)
{
    public const string SettingName = "Gemini:ApiKey";
    public const int MaxLength = 200;

    private readonly IDataProtector protector = protection.CreateProtector("GymTracker.Settings.GeminiApiKey");

    public async Task<string?> GetAsync(CancellationToken ct) => (await ResolveAsync(ct)).Key;

    public async Task<GeminiKeyStatus> StatusAsync(CancellationToken ct)
    {
        var (key, source) = await ResolveAsync(ct);
        return new GeminiKeyStatus(source, key is { Length: >= 8 } ? key[^4..] : null);
    }

    public async Task SetAsync(string key, CancellationToken ct)
    {
        key = key.Trim();
        if (key.Length is < 20 or > MaxLength || key.Any(char.IsWhiteSpace))
        {
            throw new Features.ValidationException("That doesn't look like an API key.");
        }

        var setting = await db.AppSettings.SingleOrDefaultAsync(s => s.Name == SettingName, ct);
        if (setting is null)
        {
            setting = new AppSetting { Name = SettingName };
            db.AppSettings.Add(setting);
        }

        setting.Value = protector.Protect(key);
        setting.UpdatedAt = time.GetUtcNow();
        setting.UpdatedByUserId = currentUser.UserId;
        await db.SaveChangesAsync(ct);
    }

    public async Task ClearAsync(CancellationToken ct) =>
        await db.AppSettings.Where(s => s.Name == SettingName).ExecuteDeleteAsync(ct);

    private async Task<(string? Key, KeySource Source)> ResolveAsync(CancellationToken ct)
    {
        var stored = await db.AppSettings.AsNoTracking()
            .Where(s => s.Name == SettingName)
            .Select(s => s.Value)
            .SingleOrDefaultAsync(ct);
        if (stored is not null)
        {
            try
            {
                return (protector.Unprotect(stored), KeySource.Settings);
            }
            catch (CryptographicException)
            {
                // The Data Protection keys were lost or replaced; the key has to be entered again.
                log.LogWarning("The Gemini key saved in Settings can no longer be decrypted; ignoring it");
            }
        }

        var fromEnvironment = options.Value.ApiKey?.Trim();
        return string.IsNullOrEmpty(fromEnvironment) ? (null, KeySource.None) : (fromEnvironment, KeySource.Environment);
    }
}

/// <summary>Uses Gemini when a key is available, otherwise the simple built-in parser.</summary>
public class SwitchingFoodParser(GeminiKeyStore keys, IServiceProvider services) : GymTracker.Core.Nutrition.IFoodParser
{
    public async Task<GymTracker.Core.Nutrition.FoodParseResult> ParseAsync(string text, CancellationToken ct) =>
        await keys.GetAsync(ct) is { } key
            ? await services.GetRequiredService<GeminiFoodParser>().ParseAsync(text, key, ct)
            : await new FakeFoodParser().ParseAsync(text, ct);
}
