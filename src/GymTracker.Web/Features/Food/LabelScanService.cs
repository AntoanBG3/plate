using System.Text.Json;
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Core.Text;
using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Food;

public record LabelScanResult(int ScanId, LabelValues? Values, LocalText? Error);

/// <summary>Reads a label photo, stores the scan with its photo, and records what the user saved (SPEC 6.6).</summary>
public class LabelScanService(AppDbContext db, ILabelReader reader, ICurrentUser currentUser, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<bool> IsAvailableAsync(CancellationToken ct) => reader.IsAvailableAsync(ct);

    public async Task<LabelScanResult> ReadAsync(byte[] bytes, int width, int height, CancellationToken ct)
    {
        if (bytes.Length > LabelScanPhoto.MaxBytes)
        {
            throw new ValidationException("That photo is too large.");
        }

        var type = ImageSniffer.ContentType(bytes) ?? throw new ValidationException("Attach a photo (JPEG, PNG or WebP).");
        var started = time.GetTimestamp();
        // Not cancelled with the request: a second photo or leaving the page aborts it, but the model may already
        // have been paid for, and every read is recorded (SPEC 6.6). The reader's own timeouts bound the wait.
        var result = await reader.ReadAsync([new LabelImage(bytes, type)], CancellationToken.None);
        var outcome = result.Reading is { } reading ? LabelValues.Normalize(reading) : new LabelOutcome(null, result.Error);
        var scan = new LabelScan
        {
            UserId = currentUser.UserId ?? throw new InvalidOperationException("No signed-in user."),
            CreatedAt = time.GetUtcNow(),
            Model = string.IsNullOrEmpty(result.Model) ? "unknown" : result.Model,
            LatencyMs = (int)time.GetElapsedTime(started).TotalMilliseconds,
            Error = (result.Error ?? outcome.Error)?.ToString(),
            Reading = result.Raw is null ? null : ValidJson(result.Raw),
            Values = outcome.Values is null ? null : JsonSerializer.Serialize(StoredLabelValues.From(outcome.Values), Json),
            Photos = [new LabelScanPhoto { Position = 0, ContentType = type, Width = Math.Clamp(width, 0, 10_000), Height = Math.Clamp(height, 0, 10_000), Bytes = bytes }],
        };
        db.LabelScans.Add(scan);
        await db.SaveChangesAsync(CancellationToken.None);
        return new LabelScanResult(scan.Id, outcome.Values, outcome.Error);
    }

    /// <summary>Links the saved food to its scan and notes whether any value was changed. A missing or foreign scan id is ignored.</summary>
    public async Task RecordSaveAsync(int? scanId, FoodItem saved, CancellationToken ct)
    {
        if (scanId is null || await db.LabelScans.SingleOrDefaultAsync(s => s.Id == scanId, ct) is not { Values: { } json } scan)
        {
            return;
        }

        var suggested = JsonSerializer.Deserialize<StoredLabelValues>(json, Json)!.ToValues();
        scan.FoodItemId = saved.Id;
        scan.Saved = JsonSerializer.Serialize(new
        {
            saved.KcalPer100g,
            saved.ProteinPer100g,
            saved.CarbsPer100g,
            saved.FatPer100g,
            saved.SaturatedFatPer100g,
            saved.FiberPer100g,
            saved.DefaultServingGrams,
        }, Json);
        scan.Edited = LabelValues.Edited(suggested, saved.Per100(), saved.DefaultServingGrams);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>jsonb only takes JSON; an unreadable answer is kept as a string inside an object.</summary>
    private static string ValidJson(string raw)
    {
        try
        {
            JsonDocument.Parse(raw).Dispose();
            return raw;
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { unreadable = raw.Length > 2000 ? raw[..2000] : raw });
        }
    }
}

/// <summary>LabelValues as stored in LabelScan.Values: notes as their English text, since LocalText's arguments don't round-trip.</summary>
internal record StoredLabelValues(
    string? Name,
    string? Brand,
    decimal? Kcal,
    decimal? Protein,
    decimal? Carbs,
    decimal? Fat,
    decimal? SaturatedFat,
    decimal? Fiber,
    decimal? ServingGrams,
    LabelField Converted,
    string[] Notes,
    string[] Warnings)
{
    public static StoredLabelValues From(LabelValues v) => new(v.Name, v.Brand, v.Kcal, v.Protein, v.Carbs, v.Fat, v.SaturatedFat, v.Fiber,
        v.ServingGrams, v.Converted, v.Notes.Select(n => n.ToString()).ToArray(), v.Warnings.Select(w => w.ToString()).ToArray());

    public LabelValues ToValues() => new(Name, Brand, Kcal, Protein, Carbs, Fat, SaturatedFat, Fiber, ServingGrams, Converted, [], []);
}
