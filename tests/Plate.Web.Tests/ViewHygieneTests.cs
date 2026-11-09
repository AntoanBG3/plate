using System.Text.RegularExpressions;
using FluentAssertions;

namespace Plate.Web.Tests;

/// <summary>The CSP's script-src has no 'unsafe-inline', so pages must not depend on inline script.</summary>
public class ViewHygieneTests
{
    private static List<string> Offenders(string pattern) =>
        Directory.EnumerateFiles(Path.Combine(RepoPaths.Web, "Views"), "*.cshtml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(RepoPaths.Web, "wwwroot"), "*.html"))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => Regex.IsMatch(x.line, pattern, RegexOptions.IgnoreCase))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
            .ToList();

    [Fact]
    public void Views_have_no_inline_event_handlers() =>
        Offenders(@"\son[a-z]+\s*=\s*""").Should().BeEmpty();

    // JSON data blocks are not executed and are fine.
    [Fact]
    public void Pages_have_no_inline_scripts() =>
        Offenders(@"<script(?![^>]*(\bsrc=|type=""application/json""))[^>]*>").Should().BeEmpty();

    // The sliding segment thumb has one view-transition-name; two on a page make the browser skip the whole transition
    // and log an error (app.css). Settings got a second one once (Phase 11).
    [Fact]
    public void A_view_has_at_most_one_sliding_segment_thumb() =>
        Directory.EnumerateFiles(Path.Combine(RepoPaths.Web, "Views"), "*.cshtml", SearchOption.AllDirectories)
            .Where(f => Regex.Matches(File.ReadAllText(f), @"\bdata-thumb\b").Count > 1)
            .Select(Path.GetFileName)
            .Should().BeEmpty();

    // The admin area shows what users typed and what the parser made of it; all of it must go through Razor's encoding.
    [Fact]
    public void Admin_views_never_write_raw_html() =>
        Directory.EnumerateFiles(Path.Combine(RepoPaths.Web, "Views"), "*.cshtml", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(Path.GetDirectoryName(f))!.StartsWith("Admin", StringComparison.Ordinal) || Path.GetFileName(f).Contains("Admin") || Path.GetFileName(f) == "_ReportRows.cshtml")
            .Where(f => File.ReadAllText(f).Contains("Html.Raw"))
            .Should().BeEmpty();
}
