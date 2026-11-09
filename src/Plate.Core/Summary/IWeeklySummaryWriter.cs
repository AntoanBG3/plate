using Plate.Core.Text;

namespace Plate.Core.Summary;

/// <summary>A weekly summary as shown: a few sentences, up to three tips and a line of encouragement.</summary>
public record WeeklySummaryText(string Summary, IReadOnlyList<string> Tips, string Motivation)
{
    public const int MaxSummary = 400;
    public const int MaxTips = 3;
    public const int MaxLine = 160;

    /// <summary>Trims the model's answer to size; null when there is no summary to show.</summary>
    public static WeeklySummaryText? Clean(string? summary, IEnumerable<string?> tips, string? motivation)
    {
        var s = Cut(summary, MaxSummary);
        if (s.Length == 0)
        {
            return null;
        }

        var kept = tips.Select(t => Cut(t, MaxLine)).Where(t => t.Length > 0).Take(MaxTips).ToList();
        return new WeeklySummaryText(s, kept, Cut(motivation, MaxLine));
    }

    private static string Cut(string? text, int max)
    {
        var t = (text ?? "").Trim();
        return t.Length <= max ? t : t[..(max - 1)].TrimEnd() + "…";
    }
}

/// <param name="Error">Why there is no text, for logs and the admin test.</param>
public record WeeklySummaryResult(WeeklySummaryText? Text, string? Model, LocalText? Error)
{
    public static WeeklySummaryResult Failed(LocalText error) => new(null, null, error);
}

/// <summary>Writes a weekly summary from a week's numbers (SPEC 6.5).</summary>
public interface IWeeklySummaryWriter
{
    /// <summary>False when no key is set or the weekly model is off.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct);

    Task<WeeklySummaryResult> WriteAsync(WeekFacts facts, bool bulgarian, CancellationToken ct);
}
