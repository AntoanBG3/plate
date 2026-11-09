using System.Globalization;
using System.Text;

namespace Plate.Web.Features.Export;

/// <summary>Writes RFC 4180 CSV with invariant-culture numbers, so files open the same in any locale.</summary>
public sealed class Csv
{
    private readonly StringBuilder text = new();

    public Csv(params string[] header) => Row(header);

    public Csv Row(params object?[] cells)
    {
        text.AppendJoin(',', cells.Select(Cell)).Append("\r\n");
        return this;
    }

    public override string ToString() => text.ToString();

    private static string Cell(object? value) => value switch
    {
        null => "",
        string s => Quote(Defuse(s)),
        bool b => b ? "true" : "false",
        decimal m => m.ToString("0.##########", CultureInfo.InvariantCulture), // no trailing zeros from the column scale
        DateTimeOffset t => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Quote(Defuse(value.ToString() ?? "")),
    };

    // A spreadsheet runs text starting with these characters as a formula; a leading apostrophe keeps it text.
    private static string Defuse(string s) =>
        s.Length > 0 && "=+-@\t\r".Contains(s[0]) ? "'" + s : s;

    private static string Quote(string s) =>
        s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
