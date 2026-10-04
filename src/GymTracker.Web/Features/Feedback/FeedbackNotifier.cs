using System.Threading.Channels;
using GymTracker.Core.Domain;
using GymTracker.Core.Text;
using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Features.Feedback;

/// <summary>Tells Antoan about new reports, new errors and admin unlocks, without making the request wait (SPEC 11, 12).</summary>
public interface IFeedbackNotifier
{
    void Enqueue(int feedbackId);

    void EnqueueError(int errorEventId);

    /// <summary>A short line of the app's own making, such as an admin unlock (SPEC 12). Never a secret or user data.</summary>
    void EnqueueText(string text);
}

internal enum NoticeKind
{
    Feedback,
    Error,
    Text,
}

internal readonly record struct Notice(NoticeKind Kind, int Id, string? Text = null);

public class TelegramOptions
{
    public string? BotToken { get; set; }
    public string? ChatId { get; set; }
    public string BaseUrl { get; set; } = "https://api.telegram.org/";

    public bool Enabled => !string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(ChatId);
}

/// <summary>Queues notices for <see cref="FeedbackNotifierWorker"/>; does nothing when Telegram isn't configured.</summary>
public sealed class FeedbackNotifier(Microsoft.Extensions.Options.IOptions<TelegramOptions> options) : IFeedbackNotifier
{
    internal readonly Channel<Notice> Queue = Channel.CreateBounded<Notice>(
        new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public void Enqueue(int feedbackId)
    {
        if (options.Value.Enabled)
        {
            Queue.Writer.TryWrite(new Notice(NoticeKind.Feedback, feedbackId));
        }
    }

    public void EnqueueError(int errorEventId)
    {
        if (options.Value.Enabled)
        {
            Queue.Writer.TryWrite(new Notice(NoticeKind.Error, errorEventId));
        }
    }

    public void EnqueueText(string text)
    {
        if (options.Value.Enabled)
        {
            Queue.Writer.TryWrite(new Notice(NoticeKind.Text, 0, text));
        }
    }
}

/// <summary>
/// Sends queued notices to Telegram. Only the id, the reporter's name, kind, area and the start of the message go
/// out, never attachments, snapshots or food text: Telegram is an outside service.
/// </summary>
public sealed class FeedbackNotifierWorker(FeedbackNotifier notifier, IServiceScopeFactory scopes, IHttpClientFactory http,
    Microsoft.Extensions.Options.IOptions<TelegramOptions> options, ILogger<FeedbackNotifierWorker> log) : BackgroundService
{
    public const string HttpClientName = "telegram";
    private const int PreviewLength = 80;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var notice in notifier.Queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var text = notice.Kind == NoticeKind.Text ? notice.Text : await TextAsync(notice.Kind == NoticeKind.Error, notice.Id, stoppingToken);
                if (text is not null)
                {
                    await SendAsync(text, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                log.LogWarning(ex, "Telegram notice for {Kind} {Id} failed.", notice.Kind, notice.Id);
            }
        }
    }

    private async Task<string?> TextAsync(bool isError, int id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (isError)
        {
            return await db.ErrorEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct) is { } error ? ErrorText(error) : null;
        }

        return await FeedbackTextAsync(db, id, ct);
    }

    private static async Task<string?> FeedbackTextAsync(AppDbContext db, int id, CancellationToken ct)
    {
        var f = await db.Feedback.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (f is null)
        {
            return null;
        }

        var name = await db.Profiles.IgnoreQueryFilters().Where(p => p.UserId == f.UserId).Select(p => p.DisplayName).FirstOrDefaultAsync(ct) ?? "?";
        var screenshot = await db.FeedbackAttachments.IgnoreQueryFilters().AnyAsync(a => a.FeedbackId == id, ct);
        return FormatFeedback(f, name, screenshot);
    }

    internal static string FormatFeedback(Core.Domain.Feedback f, string reporter, bool screenshot)
    {
        var head = f.Source == FeedbackSource.ParseRating
            ? $"Parse rated down by {reporter} (#{f.Id})"
            : $"Feedback #{f.Id} from {reporter}";
        var lines = new List<string> { head, $"{f.Kind} · {Localization.Localizer.Humanize(f.Area.ToString())}" };
        if (f.Reasons != ParseReason.None)
        {
            lines.Add(string.Join(", ", Enum.GetValues<ParseReason>().Where(r => r != ParseReason.None && f.Reasons.HasFlag(r)).Select(r => Localization.Localizer.Humanize(r.ToString()))));
        }

        if (f.Message is { Length: > 0 } message)
        {
            lines.Add(message.Length > PreviewLength ? message[..PreviewLength] + "…" : message);
        }

        if (f.Source is not (FeedbackSource.Settings or FeedbackSource.ParseRating))
        {
            lines.Add($"From: {f.Source}");
        }

        if (screenshot)
        {
            lines.Add("With a screenshot");
        }

        return string.Join('\n', lines);
    }

    private static string ErrorText(ErrorEvent e) =>
        $"New {e.Origin.ToString().ToLowerInvariant()} error #{e.Id}: {e.Type}\n{ErrorFingerprint.PathTemplate(e.Path)}\nVersion {e.LastVersion}";

    private async Task SendAsync(string text, CancellationToken ct)
    {
        var o = options.Value;
        using var client = http.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(5);
        // Not new Uri(base, relative): "bot123:abc/..." would read as a URI with the scheme "bot123".
        var url = new Uri($"{o.BaseUrl.TrimEnd('/')}/bot{o.BotToken}/sendMessage");
        using var response = await client.PostAsJsonAsync(url, new { chat_id = o.ChatId, text, disable_web_page_preview = true }, ct);
        if (!response.IsSuccessStatusCode)
        {
            // The token is in the URL, so log only the status.
            log.LogWarning("Telegram answered {Status}.", (int)response.StatusCode);
        }
    }
}
