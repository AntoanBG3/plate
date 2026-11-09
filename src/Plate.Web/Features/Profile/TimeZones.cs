namespace Plate.Web.Features.Profile;

/// <summary>The IANA time zones offered in Settings, grouped by region, from the server's time zone data.</summary>
public static class TimeZones
{
    private static readonly string[] Regions =
        ["Europe", "Africa", "America", "Asia", "Atlantic", "Australia", "Indian", "Pacific", "Antarctica", "Arctic"];

    private static readonly Lazy<IReadOnlyList<string>> All = new(() => TimeZoneInfo.GetSystemTimeZones()
        .Select(z => z.Id)
        .Where(id => Regions.Any(r => id.StartsWith(r + "/", StringComparison.Ordinal)))
        .Order(StringComparer.Ordinal)
        .ToList());

    /// <summary>Regions in a fixed order (Europe first), each with its zones; <paramref name="current"/> is always included.</summary>
    public static IReadOnlyList<(string Region, IReadOnlyList<string> Ids)> Grouped(string current)
    {
        var ids = All.Value.Contains(current) ? All.Value : [.. All.Value, current];
        return ids
            .GroupBy(id => Regions.FirstOrDefault(r => id.StartsWith(r + "/", StringComparison.Ordinal)) ?? "")
            .OrderBy(g => g.Key.Length == 0 ? int.MaxValue : Array.IndexOf(Regions, g.Key))
            .Select(g => (g.Key, (IReadOnlyList<string>)g.Order(StringComparer.Ordinal).ToList()))
            .ToList();
    }

    /// <summary>"America/Argentina/Buenos_Aires" under America reads "Argentina / Buenos Aires".</summary>
    public static string Label(string region, string id) =>
        (region.Length > 0 && id.Length > region.Length ? id[(region.Length + 1)..] : id).Replace('_', ' ').Replace("/", " / ");
}
