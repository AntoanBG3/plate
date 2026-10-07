using System.Globalization;

namespace GymTracker.Web.Features.Feedback;

/// <summary>The "since" windows the feedback CLI and the admin lists take (SPEC 11): 7d, 12h or a date.</summary>
public static class TimeWindow
{
    /// <summary>The start of the window; with nothing given, <paramref name="fallback"/> back from now, or null without one.</summary>
    public static DateTimeOffset? Since(string? value, TimeSpan? fallback, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(value))
        {
            return fallback is { } span ? now - span : null;
        }

        if (value.Length > 1 && int.TryParse(value[..^1], CultureInfo.InvariantCulture, out var n))
        {
            switch (value[^1])
            {
                case 'd': return now.AddDays(-n);
                case 'h': return now.AddHours(-n);
            }
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : throw new FormatException($"Can't read --since {value}; use 7d, 12h or 2026-10-01.");
    }

    /// <summary>For the admin lists: a value that can't be read falls back instead of failing the page.</summary>
    public static DateTimeOffset SinceOrDefault(string? value, TimeSpan fallback, DateTimeOffset now)
    {
        try
        {
            return Since(value, fallback, now)!.Value;
        }
        catch (FormatException)
        {
            return now - fallback;
        }
    }
}
