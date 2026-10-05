using System.Text.Json;
using System.Text.Json.Serialization;
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Core.Onboarding;
using GymTracker.Core.Text;
using GymTracker.Web.Data;
using GymTracker.Web.Features.Body;
using GymTracker.Web.Features.Profile;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Onboarding;

/// <summary>The starting maintenance and today's targets the summary card shows for the draft.</summary>
public record OnboardingPreview(int MaintenanceKcal, int? TargetKcal, int? ProteinG, bool FloorApplied, MacroTarget? Macros = null);

/// <param name="Available">A model is set up; otherwise only the form is offered.</param>
/// <param name="Started">The chat has begun; before that the splash shows.</param>
public record OnboardingPage(
    bool Available,
    bool Started,
    IReadOnlyList<ChatLine> Messages,
    IReadOnlyList<string> QuickReplies,
    OnboardingDraft Draft,
    OnboardingPreview? Preview,
    bool LimitReached,
    int CurrentYear)
{
    public bool Complete => Draft.IsComplete;
}

/// <summary>What the summary card or the plain form sends when the user taps Save.</summary>
public record OnboardingForm(
    string? DisplayName,
    Sex? Sex,
    int? BirthYear,
    int? HeightCm,
    decimal? WeightKg,
    ActivityLevel? ActivityLevel,
    Goal? Goal,
    decimal? TargetRatePercentPerWeek,
    decimal? ProteinGramsPerKg,
    int? InitialMaintenanceKcal,
    string? TimeZoneId);

/// <summary>
/// The first sign-in setup chat (SPEC 6.4). The server keeps the draft; the model only proposes values, which are
/// checked against the profile limits. Nothing reaches the profile until the user saves the summary.
/// </summary>
public class OnboardingService(
    AppDbContext db,
    ICurrentUser currentUser,
    ProfileService profiles,
    BodyweightService bodyweights,
    IOnboardingAssistant assistant,
    TimeProvider time,
    ILogger<OnboardingService> log)
{
    public const int MaxMessageLength = 500;

    /// <summary>After this many user messages the chat stops and the summary or form is the way on.</summary>
    public const int MaxTurns = 30;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private record StoredRejection(OnboardingField Field, string Reason);

    public async Task<OnboardingPage> PageAsync(bool restart, CancellationToken ct)
    {
        var chat = restart ? null : await db.OnboardingChats.SingleOrDefaultAsync(ct);
        var draft = chat is null ? await SeedAsync(ct) : Read<OnboardingDraft>(chat.Draft) ?? await SeedAsync(ct);
        return await PageForAsync(chat, draft, ct);
    }

    /// <summary>Starts a fresh chat with the model's greeting (replacing any earlier one).</summary>
    public async Task<LocalText?> StartAsync(CancellationToken ct)
    {
        await db.OnboardingChats.Where(c => c.UserId == UserId).ExecuteDeleteAsync(ct);
        var chat = new OnboardingChat { UserId = UserId, Draft = Write(await SeedAsync(ct)), UpdatedAt = time.GetUtcNow() };
        db.OnboardingChats.Add(chat);
        var error = await AskAsync(chat, ct);
        await db.SaveChangesAsync(ct);
        return error;
    }

    /// <summary>Sends the user's message and stores the model's reply. Returns why the model didn't answer, if it didn't.</summary>
    public async Task<LocalText?> SendAsync(string? text, CancellationToken ct)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new ValidationException("Type a message first.");
        }

        if (text.Length > MaxMessageLength)
        {
            throw new ValidationException(new LocalText("Keep a message under {0} characters.", MaxMessageLength));
        }

        var chat = await db.OnboardingChats.SingleOrDefaultAsync(ct);
        if (chat is null)
        {
            await StartAsync(ct);
            chat = await db.OnboardingChats.SingleAsync(ct);
        }

        if (chat.Turns >= MaxTurns)
        {
            throw new ValidationException("That's the end of the chat. Check the summary and save, or use the form.");
        }

        var lines = Read<List<ChatLine>>(chat.Messages) ?? [];
        lines.Add(new ChatLine(true, text));
        chat.Messages = Write(lines);
        chat.Turns++;
        var error = await AskAsync(chat, ct);
        await db.SaveChangesAsync(ct);
        return error;
    }

    /// <summary>Saves the summary through the same checks as Settings, logs the weight if it changed, and ends the setup.</summary>
    public async Task FinishAsync(OnboardingForm form, CancellationToken ct)
    {
        var draft = new OnboardingDraft
        {
            Name = form.DisplayName?.Trim(),
            Sex = form.Sex,
            BirthYear = form.BirthYear,
            HeightCm = form.HeightCm,
            WeightKg = form.WeightKg,
            ActivityLevel = form.ActivityLevel,
            Goal = form.Goal,
            RatePercentPerWeek = form.TargetRatePercentPerWeek,
        };
        if (!draft.IsComplete)
        {
            throw new ValidationException("Fill in every field to finish, or skip for now.");
        }

        if (ProfileRules.WeightError(form.WeightKg) is { } weightError)
        {
            throw new ValidationException(weightError);
        }

        var latest = await bodyweights.LatestAsync(ct);
        var now = time.GetUtcNow();
        var today = await profiles.TodayAsync(ct);
        await profiles.UpdateAsync(p =>
        {
            p.DisplayName = draft.Name ?? "";
            p.Sex = draft.Sex;
            p.BirthYear = draft.BirthYear;
            p.HeightCm = draft.HeightCm;
            p.ActivityLevel = draft.ActivityLevel;
            var previousGoal = p.Goal;
            p.Goal = draft.Goal!.Value;
            CutCheck.GoalChanged(p, previousGoal, today);
            p.TargetRatePercentPerWeek = draft.Goal == Goal.Maintain ? 0 : draft.RatePercentPerWeek ?? 0;
            p.ProteinGramsPerKg = form.ProteinGramsPerKg;
            p.InitialMaintenanceKcal = form.InitialMaintenanceKcal;
            if (IsTimeZone(form.TimeZoneId))
            {
                p.TimeZoneId = form.TimeZoneId!;
            }

            p.OnboardedAt = now;
        }, ct);

        if (latest?.WeightKg != decimal.Round(form.WeightKg!.Value, 2))
        {
            await bodyweights.SetAsync(await profiles.TodayAsync(ct), form.WeightKg.Value, ct);
        }

        await db.OnboardingChats.Where(c => c.UserId == UserId).ExecuteDeleteAsync(ct);
    }

    /// <summary>Ends the setup without saving anything else; Settings can start it again.</summary>
    public async Task SkipAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await profiles.UpdateAsync(p => p.OnboardedAt ??= now, ct);
        await db.OnboardingChats.Where(c => c.UserId == UserId).ExecuteDeleteAsync(ct);
    }

    /// <summary>One model turn: merges the proposed values into the draft and appends the reply.</summary>
    private async Task<LocalText?> AskAsync(OnboardingChat chat, CancellationToken ct)
    {
        var draft = Read<OnboardingDraft>(chat.Draft) ?? await SeedAsync(ct);
        var lines = Read<List<ChatLine>>(chat.Messages) ?? [];
        var rejected = (Read<List<StoredRejection>>(chat.Rejected) ?? [])
            .Select(r => new OnboardingRejection(r.Field, r.Reason))
            .ToList();
        var year = time.GetUtcNow().Year;

        var turn = await assistant.NextAsync(
            new OnboardingContext(draft, draft.Missing(), rejected, lines.ToArray(), Localization.Localizer.IsBulgarian, year), ct);
        chat.UpdatedAt = time.GetUtcNow();
        if (turn.Reply is null)
        {
            log.LogWarning("Setup chat turn failed: {Error}", turn.Error);
            chat.QuickReplies = "[]";
            return turn.Error ?? "The assistant isn't available right now. Use the form instead.";
        }

        var (merged, refused) = draft.Merge(turn.Fields, year);
        lines.Add(new ChatLine(false, turn.Reply));
        chat.Draft = Write(merged);
        chat.Messages = Write(lines);
        chat.Rejected = Write(refused.Select(r => new StoredRejection(r.Field, r.Reason.ToString())).ToList());
        chat.QuickReplies = Write(turn.QuickReplies);
        chat.Model = turn.Model;
        return null;
    }

    private async Task<OnboardingPage> PageForAsync(OnboardingChat? chat, OnboardingDraft draft, CancellationToken ct)
    {
        var year = time.GetUtcNow().Year;
        return new OnboardingPage(
            await assistant.IsAvailableAsync(ct),
            chat is not null,
            chat is null ? [] : Read<List<ChatLine>>(chat.Messages) ?? [],
            chat is null ? [] : Read<List<string>>(chat.QuickReplies) ?? [],
            draft,
            Preview(draft, await profiles.GetAsync(ct), DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime)),
            chat?.Turns >= MaxTurns,
            year);
    }

    /// <summary>The targets the draft would give today, once there is enough to compute them.</summary>
    public static OnboardingPreview? Preview(OnboardingDraft draft, UserProfile profile, DateOnly today)
    {
        var applied = draft.ApplyTo(profile);
        if (Maintenance.Prior(applied, draft.WeightKg, today.Year) is not { } maintenance)
        {
            return null;
        }

        var target = GoalTargets.Calculate(maintenance, applied, draft.WeightKg, today);
        return new OnboardingPreview(maintenance, target.Kcal, target.ProteinG, target.FloorApplied, target.Macros);
    }

    private async Task<OnboardingDraft> SeedAsync(CancellationToken ct) =>
        OnboardingDraft.FromProfile(await profiles.GetAsync(ct), (await bodyweights.LatestAsync(ct))?.WeightKg);

    private static bool IsTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
        {
            return false;
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
    }

    private string UserId => currentUser.UserId ?? throw new InvalidOperationException("No signed-in user.");

    private static T? Read<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string Write<T>(T value) => JsonSerializer.Serialize(value, Json);
}

/// <summary>The summary card under the chat, or the whole page when the user picks the form.</summary>
/// <param name="Summary">Under the chat, with the targets preview; otherwise the plain form.</param>
public record SetupFormModel(OnboardingDraft Draft, OnboardingPreview? Preview, int CurrentYear, string ReturnUrl, bool Summary);
