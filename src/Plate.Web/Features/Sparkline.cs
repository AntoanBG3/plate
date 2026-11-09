using System.Globalization;
using Plate.Core.Nutrition;

namespace Plate.Web.Features;

/// <summary>SVG paths for a small trend sparkline: the trend as a smooth line and area, weigh-ins as dots.</summary>
public record Sparkline(string Line, string Area, IReadOnlyList<(double X, double Y)> Dots)
{
    public const double Width = 128;
    public const double Height = 52;

    public static Sparkline? From(IReadOnlyList<TrendPoint> points)
    {
        if (points.Count < 2)
        {
            return null;
        }

        var values = points.Select(p => (double)p.TrendKg).Concat(points.Where(p => p.RawKg is not null).Select(p => (double)p.RawKg!.Value)).ToList();
        var min = values.Min();
        var max = values.Max();
        var span = Math.Max(0.6, max - min); // a flat trend still sits in the middle, not stretched to the edges
        var mid = (min + max) / 2;
        double X(int i) => i * Width / (points.Count - 1);
        double Y(double kg) => 4 + (Height - 8) * (1 - ((kg - mid) / span + 0.5));

        var trend = points.Select((p, i) => (X: X(i), Y: Y((double)p.TrendKg))).ToList();
        var line = "M" + string.Join(" L", trend.Select(p => Pt(p.X, p.Y)));
        var area = line + $" L{Pt(Width, Height)} L{Pt(0, Height)} Z";
        var dots = points.Select((p, i) => (p, i)).Where(x => x.p.RawKg is not null)
            .Select(x => (X(x.i), Y((double)x.p.RawKg!.Value))).ToList();
        return new Sparkline(line, area, dots);
    }

    private static string Pt(double x, double y) => string.Create(CultureInfo.InvariantCulture, $"{x:0.#} {y:0.#}");
}
