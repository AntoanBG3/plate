using Plate.Core.Text;
using Plate.Web.Features.Feedback;
using Plate.Web.Integrations;

namespace Plate.Web.Features.Admin;

/// <summary>
/// Changes to the shared OpenRouter key and AI models, for the admin area and the CLI (SPEC 12). Every change is
/// audited, and a key change also sends a Telegram alert. The key is never returned, logged or audited.
/// </summary>
public class AiAdmin(OpenRouterSettings settings, OpenRouterFoodParser parser, JevFoodMatchDecider matcher, OpenRouterOnboardingAssistant onboarding, OpenRouterWeeklySummaryWriter weekly, OpenRouterLabelReader label, OpenRouterFoodEstimator estimator, AdminAuditLog audit, IFeedbackNotifier notifier)
{
    public async Task SetKeyAsync(string key, CancellationToken ct)
    {
        await settings.SetKeyAsync(key, ct);
        await audit.WriteAsync(AuditActions.KeySet, null, null, ct);
        notifier.EnqueueText($"{Brand.Name}: the OpenRouter key was changed from {audit.Origin}.");
    }

    public async Task ClearKeyAsync(CancellationToken ct)
    {
        await settings.ClearKeyAsync(ct);
        await audit.WriteAsync(AuditActions.KeyRemove, null, null, ct);
        notifier.EnqueueText($"{Brand.Name}: the OpenRouter key was removed from {audit.Origin}.");
    }

    public async Task SetModelAsync(AiTask task, string id, CancellationToken ct)
    {
        var before = await settings.GetModelAsync(task, ct);
        await settings.SetModelAsync(task, id, ct);
        await audit.WriteAsync(AuditActions.ModelSet, AiTasks.Slug(task), $"{before} -> {id}", ct);
    }

    public async Task SetEffortAsync(AiTask task, string effort, CancellationToken ct)
    {
        var before = await settings.GetEffortAsync(task, ct) ?? AiTasks.LowestEffort;
        await settings.SetEffortAsync(task, effort, ct);
        await audit.WriteAsync(AuditActions.EffortSet, AiTasks.Slug(task), $"{before} -> {await settings.GetEffortAsync(task, ct) ?? AiTasks.LowestEffort}", ct);
    }

    /// <summary>Runs the task's own check (the same as after picking its model): null when it works or the task is off.</summary>
    public Task<LocalText?> TestAsync(AiTask task, CancellationToken ct) => task switch
    {
        AiTask.Parser => TestParserAsync(ct),
        AiTask.Matcher => TestMatcherAsync(ct),
        AiTask.Onboarding => TestOnboardingAsync(ct),
        AiTask.Weekly => TestWeeklyAsync(ct),
        AiTask.Estimator => TestEstimatorAsync(ct),
        _ => TestLabelAsync(ct),
    };

    /// <summary>Parses "1 banana" with the current key and parser model: null when it works, otherwise why not.</summary>
    public async Task<LocalText?> TestParserAsync(CancellationToken ct)
    {
        if (await settings.GetKeyAsync(ct) is not { } key)
        {
            return new LocalText("No OpenRouter key is set.");
        }

        var error = await parser.TestAsync(key, await settings.GetModelAsync(AiTask.Parser, ct), ct, await settings.GetEffortAsync(AiTask.Parser, ct));
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

        var error = await onboarding.TestAsync(key, model, ct, await settings.GetEffortAsync(AiTask.Onboarding, ct));
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

        var error = await weekly.TestAsync(key, model, ct, await settings.GetEffortAsync(AiTask.Weekly, ct));
        await audit.WriteAsync(AuditActions.KeyTest, AiTasks.Slug(AiTask.Weekly), error is null ? "works" : "fails", ct);
        return error;
    }

    /// <summary>Reads the bundled sample label with the label model: null when it reads it right or labels are off.</summary>
    public async Task<LocalText?> TestLabelAsync(CancellationToken ct)
    {
        var model = await settings.GetModelAsync(AiTask.Label, ct);
        if (model == AiTasks.Off)
        {
            return null;
        }

        if (await settings.GetKeyAsync(ct) is not { } key)
        {
            return new LocalText("No OpenRouter key is set.");
        }

        var error = await label.TestAsync(key, model, ct, await settings.GetEffortAsync(AiTask.Label, ct));
        await audit.WriteAsync(AuditActions.KeyTest, AiTasks.Slug(AiTask.Label), error is null ? "works" : "fails", ct);
        return error;
    }

    /// <summary>Estimates a sample pastry with the estimator model: null when it gives possible values or the estimator is off.</summary>
    public async Task<LocalText?> TestEstimatorAsync(CancellationToken ct)
    {
        var model = await settings.GetModelAsync(AiTask.Estimator, ct);
        if (model == AiTasks.Off)
        {
            return null;
        }

        if (await settings.GetKeyAsync(ct) is not { } key)
        {
            return new LocalText("No OpenRouter key is set.");
        }

        var error = await estimator.TestAsync(key, model, ct, await settings.GetEffortAsync(AiTask.Estimator, ct));
        await audit.WriteAsync(AuditActions.KeyTest, AiTasks.Slug(AiTask.Estimator), error is null ? "works" : "fails", ct);
        return error;
    }
}
