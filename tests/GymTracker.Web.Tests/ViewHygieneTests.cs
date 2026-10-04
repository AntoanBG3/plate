using System.Text.RegularExpressions;
using FluentAssertions;

namespace GymTracker.Web.Tests;

public class ViewHygieneTests
{
    private static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GymTracker.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "src", "GymTracker.Web");
    }

    private static IEnumerable<string> Pages() =>
        Directory.EnumerateFiles(Path.Combine(WebRoot(), "Views"), "*.cshtml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(WebRoot(), "wwwroot"), "*.html"));

    [Fact]
    public void Views_have_no_inline_event_handlers()
    {
        // Inline handlers need 'unsafe-inline' in the CSP script-src, which would undo most of its value.
        var offenders = Pages()
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => Regex.IsMatch(x.line, @"\son[a-z]+\s*=\s*""", RegexOptions.IgnoreCase))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
            .ToList();

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Pages_have_no_inline_scripts()
    {
        // Same reason: script-src has no 'unsafe-inline'. JSON data blocks are not executed and are fine.
        var offenders = Pages()
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => Regex.IsMatch(x.line, @"<script(?![^>]*(\bsrc=|type=""application/json""))[^>]*>", RegexOptions.IgnoreCase))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
            .ToList();

        offenders.Should().BeEmpty();
    }
}
