using GymTracker.Core.Text;
using GymTracker.Web.Features.Feedback;
using GymTracker.Web.Integrations;

namespace GymTracker.Web.Features.Admin;

/// <summary>
/// Changes to the shared OpenRouter key and AI models, for the admin area and the CLI (SPEC 12). Every change is
/// audited, and a key change also sends a Telegram alert. The key is never returned, logged or audited.
/// </summary>
public class AiAdmin(OpenRouterSettings settings, OpenRouterFoodParser parser, JevFoodMatchDecider matcher, OpenRouterOnboardingAssistant onboarding, OpenRouterWeeklySummaryWriter weekly, AdminAuditLog audit, IFeedbackNotifier notifier)
{
    public async Task SetKeyAsync(string key, CancellationToken ct)
    {
        await settings.SetKeyAsync(key, ct);
        await audit.WriteAsync(AuditActions.KeySet, null, null, ct);
        notifier.EnqueueText($"GymTracker: the OpenRouter key was changed from {audit.Origin}.");
    }

    public async Task ClearKeyAsync(CancellationToken ct)
    {
        await settings.ClearKeyAsync(ct);
        await audit.WriteAsync(AuditActions.KeyRemove, null, null, ct);
        notifier.EnqueueText($"GymTracker: the OpenRouter key was removed from {audit.Origin}.");
    }

    public async Task SetModelAsync(AiTask task, string id, CancellationToken ct)
    {
        var before = await settings.GetModelAsync(task, ct);
        await settings.SetModelAsync(task, id, ct);
        await audit.WriteAsync(AuditActions.ModelSet, AiTasks.Slug(task), $"{before} -> {id}", ct);
    }

    /// <summary>Parses "1 banana" with the current key and parser model: null when it works, otherwise why not.</summary>
    public async Task<LocalText?> TestParserAsync(CancellationToken ct)
    {
        if (await settings.GetKeyAsync(ct) is not { } key)
        {
            return new LocalText("No OpenRouter key is set.");
        }

        var error = await parser.TestAsync(key, await settings.GetModelAsync(AiTask.Parser, ct), ct);
        await audit.WriteAsync(AuditActions.KeyTest, AiTasks.Slug(AiTask.Parser), error is null ? "works" : "fails", ct);
        return error;
    }

    /// <summary>Asks the matcher model about "a banana": null when it works or the matcher is off.</summary>
    public async Task<LocalText?> TestMatcherAsync(CancellationToken ct)
    {
        var model = await settings.GetModelAsync(AiTask.Matcher, ct);
        if (model == AiTasks.Off)
        {
            return null;
        }

        if (await settings.GetKeyAsync(ct) is not { } key)
        {
            return new LocalText("No OpenRouter key is set.");
        }

        var error = await matcher.TestAsync(key, model, ct);
        await audit.WriteAsync(AuditActions.KeyTest, AiTasks.Slug(AiTask.Matcher), error is null ? "works" : "fails", ct);
        return error;
    }

    /// <summary>Opens a setup chat with the onboarding model: null when it answers or the chat is off.</summary>
    public async Task<LocalText?> TestOnboardingAsync(CancellationToken ct)
    {
        var model = await settings.GetModelAsync(AiTask.Onboarding, ct);
        if (model == AiTasks.Off)
        {
            return null;
        }

        if (await settings.GetKeyAsync(ct) is not { } key)
        {
            return new LocalText("No OpenRouter key is set.");
        }

        var error = await onboarding.TestAsync(key, model, ct);
        await audit.WriteAsync(AuditActions.KeyTest, AiTasks.Slug(AiTask.Onboarding), error is null ? "works" : "fails", ct);
        return error;
    }

    /// <summary>Writes a summary of a made-up week with the weekly model: null when it answers or the summary is off.</summary>
    public async Task<LocalText?> TestWeeklyAsync(CancellationToken ct)
    {
        var model = await settings.GetModelAsync(AiTask.Weekly, ct);
        if (model == AiTasks.Off)
        {
            return null;
        }

        if (await settings.GetKeyAsync(ct) is not { } key)
        {
            return new LocalText("No OpenRouter key is set.");
        }

        var error = await weekly.TestAsync(key, model, ct);
        await audit.WriteAsync(AuditActions.KeyTest, AiTasks.Slug(AiTask.Weekly), error is null ? "works" : "fails", ct);
        return error;
    }
}
