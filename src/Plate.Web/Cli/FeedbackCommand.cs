using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Plate.Core.Domain;
using Plate.Core.Nutrition;
using Plate.Web.Features.Feedback;

namespace Plate.Web.Cli;

/// <summary>
/// <c>dotnet Plate.Web.dll feedback &lt;command&gt;</c>: reads reports, ratings, parse runs and errors, and writes
/// statuses, replies and What's new entries (SPEC 11). Run on wizard through <c>docker exec</c>. Output goes to
/// stdout, logs to stderr; <c>--json</c> prints JSON instead of text.
/// </summary>
public static class FeedbackCommand
{
    public const string Name = "feedback";

    private const string Usage = """
        Usage: feedback <command> [options]
          list [--status new|open|all|<status>,...] [--since 7d|<date>] [--area <area>] [--ratings]
          show <id>
          attachment <id>                 raw image bytes to stdout
          status <id> <status> [--reply "..."] [--version <sha>]
                                          statuses: New Seen Planned Fixed WontFix NeedsInfo
          parses [--since 30d] [--flagged] [--limit 50] [--export-eval]
          eval [--since 365d] [--flagged] [--limit 20] [--parser <model>] [--matcher off|jev|<decision model id>]
          labels [--since 30d] [--edited] [--failed] [--limit 50]
          label <id>                      what the label model read, the values offered and what was saved
          label-photo <id> [n]            the nth photo's raw bytes (1 by default) to stdout, while kept (30 days); audited
          errors [--since 7d]
          digest [--since 7d|<date>]
          changelog add --en "..." --bg "..." [--feedback <id>] [--version <sha>]
          changelog list
          changelog remove <id>
        Add --json to any command for JSON output.
        """;

    private static readonly JsonSerializerOptions Pretty = new(FeedbackJson.Options) { WriteIndented = true };

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter? output = null, Stream? binary = null)
    {
        var o = output ?? Console.Out;
        var rest = args.Skip(1).ToArray();
        var json = rest.Contains("--json");
        if (rest.Length == 0 || rest[0] is "help" or "--help" or "-h")
        {
            o.WriteLine(Usage);
            return rest.Length == 0 ? 2 : 0;
        }

        using var scope = services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<FeedbackAdmin>();
        var labels = scope.ServiceProvider.GetRequiredService<LabelScanReport>();
        var ct = CancellationToken.None;
        try
        {
            switch (rest[0])
            {
                case "list":
                    var rows = await admin.ListAsync(Statuses(Option(rest, "--status") ?? "open"), Since(Option(rest, "--since"), null), Area(Option(rest, "--area")),
                        rest.Contains("--ratings"), ct);
                    if (json)
                    {
                        WriteJson(o, rows);
                    }
                    else
                    {
                        WriteRows(o, rows);
                    }

                    return 0;

                case "show":
                    var detail = await admin.ShowAsync(Id(rest), ct);
                    if (detail is null)
                    {
                        return NotFound(Id(rest));
                    }

                    if (json)
                    {
                        WriteJson(o, detail);
                    }
                    else
                    {
                        WriteDetail(o, detail);
                    }

                    return 0;

                case "attachment":
                    var attachment = await admin.AttachmentAsync(Id(rest), ct);
                    if (attachment is null)
                    {
                        Console.Error.WriteLine($"Feedback #{Id(rest)} has no screenshot.");
                        return 1;
                    }

                    var stream = binary ?? Console.OpenStandardOutput();
                    await stream.WriteAsync(attachment.Bytes, ct);
                    await stream.FlushAsync(ct);
                    return 0;

                case "status":
                    if (rest.Length < 3 || !Enum.TryParse<FeedbackStatus>(rest[2], ignoreCase: true, out var status))
                    {
                        Console.Error.WriteLine("Usage: feedback status <id> <New|Seen|Planned|Fixed|WontFix|NeedsInfo> [--reply \"...\"] [--version <sha>]");
                        return 2;
                    }

                    var id = Id(rest);
                    if (!await admin.SetStatusAsync(id, status, Option(rest, "--reply"), Option(rest, "--version"), ct))
                    {
                        return NotFound(id);
                    }

                    o.WriteLine($"#{id} is now {status}.");
                    if (status == FeedbackStatus.Fixed)
                    {
                        o.WriteLine("If the fix is visible to users, add a What's new line: feedback changelog add --en \"...\" --bg \"...\" --feedback " + id);
                    }

                    return 0;

                case "parses":
                    var since = Since(Option(rest, "--since"), TimeSpan.FromDays(30))!.Value;
                    if (rest.Contains("--export-eval"))
                    {
                        foreach (var c in await admin.EvalCasesAsync(since, rest.Contains("--flagged"), ct))
                        {
                            o.WriteLine(JsonSerializer.Serialize(c, FeedbackJson.Options));
                        }

                        return 0;
                    }

                    var runs = await admin.ParsesAsync(since, rest.Contains("--flagged"), Int(Option(rest, "--limit"), 50), ct);
                    if (json)
                    {
                        WriteJson(o, runs);
                    }
                    else
                    {
                        foreach (var run in runs)
                        {
                            WriteRun(o, run, brief: true);
                        }

                        o.WriteLine($"{runs.Count} parse runs.");
                    }

                    return 0;

                case "eval":
                    return await ParseEvaluation.RunAsync(scope.ServiceProvider, rest, o, json, ct);

                case "errors":
                    var errors = await admin.ErrorsAsync(Since(Option(rest, "--since"), TimeSpan.FromDays(7))!.Value, ct);
                    if (json)
                    {
                        WriteJson(o, errors);
                    }
                    else
                    {
                        WriteErrors(o, errors, withStack: true);
                    }

                    return 0;

                case "labels":
                    var scans = await labels.ListAsync(Since(Option(rest, "--since"), TimeSpan.FromDays(30))!.Value, rest.Contains("--edited"), rest.Contains("--failed"),
                        Int(Option(rest, "--limit"), 50), ct);
                    if (json)
                    {
                        WriteJson(o, scans);
                    }
                    else
                    {
                        foreach (var scan in scans)
                        {
                            o.WriteLine(LabelLine(scan));
                        }
                    }

                    return 0;

                case "label":
                    var label = await labels.GetAsync(Id(rest), ct);
                    if (label is null)
                    {
                        Console.Error.WriteLine($"There is no label scan #{Id(rest)}.");
                        return 1;
                    }

                    if (json)
                    {
                        WriteJson(o, label);
                    }
                    else
                    {
                        o.Write(LabelText(label));
                    }

                    return 0;

                case "label-photo":
                    var number = 1;
                    if (rest.Length > 2 && !(int.TryParse(rest[2], NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0))
                    {
                        Console.Error.WriteLine($"The photo number must be 1 or more, not \"{rest[2]}\".");
                        return 1;
                    }

                    var photo = await labels.PhotoAsync(Id(rest), number, ct);
                    if (photo is null)
                    {
                        Console.Error.WriteLine($"Label scan #{Id(rest)} has no photo {number} (deleted after 30 days, or no such scan or photo).");
                        return 1;
                    }

                    var photoStream = binary ?? Console.OpenStandardOutput();
                    await photoStream.WriteAsync(photo.Bytes, ct);
                    await photoStream.FlushAsync(ct);
                    return 0;

                case "digest":
                    var digest = await admin.DigestAsync(Since(Option(rest, "--since"), TimeSpan.FromDays(7))!.Value, ct);
                    if (json)
                    {
                        WriteJson(o, digest);
                    }
                    else
                    {
                        WriteDigest(o, digest);
                        var counts = await labels.CountsAsync(digest.Since, ct);
                        o.WriteLine();
                        o.WriteLine($"Label reads: {counts.Reads} ({counts.Failed} failed, {counts.Edited} edited)");
                    }

                    return 0;

                case "changelog" when rest.Length > 1 && rest[1] == "add":
                    var en = Option(rest, "--en");
                    var bg = Option(rest, "--bg");
                    if (string.IsNullOrWhiteSpace(en) || string.IsNullOrWhiteSpace(bg))
                    {
                        Console.Error.WriteLine("Usage: feedback changelog add --en \"...\" --bg \"...\" [--feedback <id>] [--version <sha>]");
                        return 2;
                    }

                    var feedbackId = Option(rest, "--feedback") is { } f ? int.Parse(f, CultureInfo.InvariantCulture) : (int?)null;
                    var entry = await admin.AddChangelogAsync(en, bg, feedbackId, Option(rest, "--version"), ct);
                    o.WriteLine($"Added What's new #{entry.Id}.");
                    return 0;

                case "changelog" when rest.Length > 1 && rest[1] == "list":
                    var entries = await admin.ChangelogAsync(100, ct);
                    if (json)
                    {
                        WriteJson(o, entries);
                    }
                    else
                    {
                        foreach (var c in entries)
                        {
                            o.WriteLine($"#{c.Id}  {When(c.CreatedAt)}{(c.FeedbackId is { } fid ? $"  feedback #{fid}" : "")}{(c.Version is { } v ? $"  {v}" : "")}");
                            o.WriteLine($"    en: {c.TextEn}");
                            o.WriteLine($"    bg: {c.TextBg}");
                        }

                        o.WriteLine($"{entries.Count} entries.");
                    }

                    return 0;

                case "changelog" when rest.Length > 2 && rest[1] == "remove":
                    var changelogId = int.TryParse(rest[2].TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var cid)
                        ? cid
                        : throw new FormatException("Give a What's new id, e.g. feedback changelog remove 3.");
                    if (!await admin.RemoveChangelogAsync(changelogId, ct))
                    {
                        Console.Error.WriteLine($"There is no What's new #{changelogId}.");
                        return 1;
                    }

                    o.WriteLine($"Removed What's new #{changelogId}.");
                    return 0;

                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    // ---- arguments ----

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int Id(string[] args) =>
        args.Length > 1 && int.TryParse(args[1].TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new FormatException("Give a feedback id, e.g. feedback show 12.");

    private static int Int(string? value, int fallback) => int.TryParse(value, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : fallback;

    /// <summary>"7d", "12h" or a date (yyyy-MM-dd); the fallback when not given.</summary>
    internal static DateTimeOffset? Since(string? value, TimeSpan? fallback) => TimeWindow.Since(value, fallback, DateTimeOffset.UtcNow);

    private static List<FeedbackStatus>? Statuses(string value) => value.ToLowerInvariant() switch
    {
        "all" => null,
        "open" => [FeedbackStatus.New, FeedbackStatus.Seen, FeedbackStatus.Planned, FeedbackStatus.NeedsInfo],
        _ => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.TryParse<FeedbackStatus>(s, ignoreCase: true, out var st) ? st : throw new FormatException($"Unknown status {s}."))
            .ToList(),
    };

    private static FeedbackArea? Area(string? value) =>
        value is null ? null
        : Enum.TryParse<FeedbackArea>(value, ignoreCase: true, out var area) ? area
        : throw new FormatException($"Unknown area {value}. Areas: {string.Join(", ", Enum.GetNames<FeedbackArea>())}.");

    private static int NotFound(int id)
    {
        Console.Error.WriteLine($"There is no feedback #{id}.");
        return 1;
    }

    // ---- output ----

    internal static void WriteJson<T>(TextWriter o, T value) => o.WriteLine(JsonSerializer.Serialize(value, Pretty));

    private static string When(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "Z";

    private static string FirstLine(string? text, int max = 70)
    {
        var line = (text ?? "").Split('\n')[0].Trim();
        return line.Length > max ? line[..max] + "…" : line;
    }

    private static string Reasons(ParseReason reasons) =>
        string.Join(", ", Enum.GetValues<ParseReason>().Where(r => r != ParseReason.None && reasons.HasFlag(r)));

    private static void WriteRows(TextWriter o, IReadOnlyList<FeedbackRow> rows)
    {
        if (rows.Count == 0)
        {
            o.WriteLine("Nothing.");
            return;
        }

        foreach (var r in rows)
        {
            var what = r.Message is not null ? FirstLine(r.Message)
                : r.Rating is { } rating ? $"[{rating}] {Reasons(r.Reasons)}".TrimEnd()
                : "";
            var flags = (r.HasScreenshot ? " +screenshot" : "") + (r.HasReply ? " +reply" : "") + (r.ParseRunId is { } run ? $" run {run}" : "");
            o.WriteLine($"#{r.Id,-4} {When(r.CreatedAt)}  {r.Reporter,-10} {r.Status,-9} {r.Source}/{r.Kind}/{r.Area}{flags}");
            if (what.Length > 0)
            {
                o.WriteLine($"      {what}");
            }
        }
    }

    private static void WriteDetail(TextWriter o, FeedbackDetail d)
    {
        var r = d.Row;
        o.WriteLine($"Feedback #{r.Id} from {r.Reporter}, {When(r.CreatedAt)}");
        o.WriteLine($"Status: {r.Status}{(d.StatusChangedAt is { } changed ? $" (changed {When(changed)})" : "")}{(d.FixedInVersion is { } v ? $", fixed in {v}" : "")}");
        o.WriteLine($"Source: {r.Source}   Kind: {r.Kind}   Area: {r.Area}{(r.Rating is { } rating ? $"   Rating: {rating}" : "")}");
        if (r.Reasons != ParseReason.None)
        {
            o.WriteLine($"Reasons: {Reasons(r.Reasons)}");
        }

        o.WriteLine();
        o.WriteLine(r.Message ?? "(no message)");
        if (d.Reply is not null)
        {
            o.WriteLine();
            o.WriteLine($"Reply: {d.Reply}");
        }

        if (d.Screenshot is { } shot)
        {
            o.WriteLine();
            o.WriteLine($"Screenshot: {shot.ContentType}, {shot.Width}x{shot.Height}, {shot.Bytes / 1024} KB (feedback attachment {r.Id} > file)");
        }

        o.WriteLine();
        o.WriteLine("Context:");
        o.WriteLine(d.Context?.ToJsonString(Pretty) ?? "{}");
        if (d.ParseRun is not null)
        {
            o.WriteLine();
            WriteRun(o, d.ParseRun, brief: false, item: d.ParseItemIndex);
        }

        if (d.Error is not null)
        {
            o.WriteLine();
            WriteErrors(o, [d.Error], withStack: true);
        }
    }

    private static void WriteRun(TextWriter o, ParseRunDetail run, bool brief, int? item = null)
    {
        o.WriteLine($"Parse run {run.Id} by {run.Reporter}, {When(run.CreatedAt)}: \"{run.Text}\"");
        o.WriteLine($"  parser {run.AiModel ?? "-"} {run.ParserMs} ms, matcher {run.MatchModel ?? "ranking"} {run.MatchMs} ms, "
            + (run.ConfirmedAt is null ? "not saved" : $"saved, corrections: {run.Corrections}"));
        if (run.ParserError is not null)
        {
            o.WriteLine($"  parser error: {run.ParserError}");
        }

        foreach (var (ratedItem, rating, reasons, message) in run.Ratings)
        {
            o.WriteLine($"  rated {rating} {(ratedItem is { } n ? $"item {n}" : "overall")}{(reasons != ParseReason.None ? ": " + Reasons(reasons) : "")}{(message is null ? "" : $" \"{FirstLine(message)}\"")}");
        }

        var resolved = run.Resolved as JsonArray ?? [];
        var saved = run.Saved as JsonArray;
        for (var n = 0; n < resolved.Count; n++)
        {
            var row = resolved[n]!;
            var mark = item == n ? "»" : " ";
            o.WriteLine($" {mark}[{n}] {row["description"]} {row["grams"]} g → {row["matchedName"] ?? "(AI estimate)"} #{row["foodItemId"]} {row["confidence"]}{(row["usual"]?.GetValue<bool>() == true ? " usual" : "")}");
            if (saved?.ElementAtOrDefault(n) is { } s)
            {
                o.WriteLine($"       saved: {(s["included"]?.GetValue<bool>() == true ? $"{s["description"]} {s["grams"]} g #{s["foodItemId"]}" : "unticked")} ({s["corrections"]})");
            }

            if (!brief && row["match"]?["top"] is JsonArray top)
            {
                foreach (var c in top)
                {
                    o.WriteLine($"       candidate #{c!["id"]} {c["name"]} score {c["score"]}{(c["own"]?.GetValue<bool>() == true ? " (own)" : "")}");
                }

                if (row["match"]?["asked"]?.GetValue<bool>() == true)
                {
                    o.WriteLine($"       decider picked {(row["match"]?["deciderPick"]?.ToString() ?? "none")} at {row["match"]?["deciderConfidence"]}");
                }
            }
        }

        if (!brief)
        {
            o.WriteLine("  parsed items:");
            o.WriteLine(Indent(run.Parsed?.ToJsonString(Pretty) ?? "[]"));
        }
    }

    private static void WriteErrors(TextWriter o, IReadOnlyList<ErrorEvent> errors, bool withStack)
    {
        if (errors.Count == 0)
        {
            o.WriteLine("No errors.");
            return;
        }

        foreach (var e in errors)
        {
            o.WriteLine($"Error #{e.Id} [{e.Origin}] {e.Type} ×{e.Count}, first {When(e.FirstSeenAt)} ({e.FirstVersion ?? "?"}), last {When(e.LastSeenAt)} ({e.LastVersion ?? "?"})");
            o.WriteLine($"  {e.Path}: {FirstLine(e.Message, 200)}");
            if (withStack && e.Stack is not null)
            {
                o.WriteLine(Indent(string.Join('\n', e.Stack.Split('\n').Take(12))));
            }
        }
    }

    private static void WriteDigest(TextWriter o, Digest d)
    {
        o.WriteLine($"Server version {Features.Diagnostics.AppVersion.Current}. Since {When(d.Since)}:");
        o.WriteLine($"  {d.Reports.Count} reports, {d.Errors.Count} errors seen, {d.Parses} parses ({d.Confirmed} saved, {d.Corrected} corrected), ratings {d.RatedUp} up / {d.RatedDown} down");
        o.WriteLine();
        o.WriteLine("Reports:");
        WriteRows(o, d.Reports);
        o.WriteLine();
        o.WriteLine("Errors:");
        WriteErrors(o, d.Errors, withStack: false);
    }

    private static string LabelLine(LabelScanRow s) =>
        $"#{s.Id} {When(s.CreatedAt)} {s.Reporter} {s.Model} {s.LatencyMs} ms "
        + (s.Error is not null ? $"failed: {s.Error}" : !s.Saved ? "not saved" : s.Edited ? "saved with edits" : "saved as read")
        + (s.PhotoCount == 0 ? " (photo deleted)" : s.PhotoCount > 1 ? $" ({s.PhotoCount} photos)" : "");

    /// <summary>A label scan as the CLI prints it, for the admin area's scan page.</summary>
    internal static string LabelText(LabelScanDetail detail)
    {
        using var w = new StringWriter();
        w.WriteLine(LabelLine(detail.Scan));
        w.WriteLine($"Read:    {detail.Reading ?? "(nothing)"}");
        w.WriteLine($"Offered: {detail.Values ?? "(nothing)"}");
        w.WriteLine($"Saved:   {detail.Saved ?? "(not saved)"}{(detail.FoodItemId is { } food ? $" as food #{food}" : "")}");
        return w.ToString();
    }

    /// <summary>A parse run as the CLI prints it, for the admin area's report and parse pages.</summary>
    internal static string RunText(ParseRunDetail run, int? item)
    {
        using var w = new StringWriter();
        WriteRun(w, run, brief: false, item);
        return w.ToString();
    }

    /// <summary>An error group with its stack, as the CLI prints it.</summary>
    internal static string ErrorText(ErrorEvent error)
    {
        using var w = new StringWriter();
        WriteErrors(w, [error], withStack: true);
        return w.ToString();
    }

    internal static string ContextText(JsonNode? context) => context?.ToJsonString(Pretty) ?? "{}";

    private static string Indent(string text) => string.Join('\n', text.Split('\n').Select(l => "    " + l));
}
