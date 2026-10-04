using System.Text.RegularExpressions;
using FluentAssertions;

namespace GymTracker.Web.Tests;

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
}
