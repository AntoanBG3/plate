using System.Globalization;

namespace GymTracker.Web.Features;

public static class Format
{
    public static string Kg(this decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Kg(this decimal value, int decimals) =>
        decimal.Round(value, decimals, MidpointRounding.AwayFromZero).ToString("0.##", CultureInfo.InvariantCulture);

    public static string Elapsed(this TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min" : $"{Math.Max(0, span.Minutes)} min";

    public static string Rest(int seconds) => $"{seconds / 60}:{seconds % 60:00}";
}
