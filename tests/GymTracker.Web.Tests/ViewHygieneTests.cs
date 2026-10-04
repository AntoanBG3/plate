using System.Text.RegularExpressions;
using FluentAssertions;

namespace GymTracker.Web.Tests;

public class ViewHygieneTests
{
    private static string ViewsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GymTracker.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "src", "GymTracker.Web", "Views");
    }

    [Fact]
    public void Views_have_no_inline_event_handlers()
    {
        // Inline handlers need 'unsafe-inline' in the CSP script-src, which would undo most of its value.
        var offenders = Directory.EnumerateFiles(ViewsRoot(), "*.cshtml", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => Regex.IsMatch(x.line, @"\son[a-z]+\s*=\s*""", RegexOptions.IgnoreCase))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
            .ToList();

        offenders.Should().BeEmpty();
    }
}
