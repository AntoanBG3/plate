using FluentAssertions;
using Plate.Web.Features.Feedback;

namespace Plate.Web.Tests;

/// <summary>The "since" windows the feedback CLI and the admin lists take: 7d, 12h or a date.</summary>
public class TimeWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("7d", "2026-09-30T12:00:00Z")]
    [InlineData("12h", "2026-10-07T00:00:00Z")]
    [InlineData("2026-10-01", "2026-10-01T00:00:00Z")]
    public void Reads_days_hours_and_dates(string value, string expected) =>
        TimeWindow.Since(value, null, Now).Should().Be(DateTimeOffset.Parse(expected));

    [Fact]
    public void Nothing_given_is_the_fallback_or_null()
    {
        TimeWindow.Since(null, TimeSpan.FromDays(30), Now).Should().Be(Now.AddDays(-30));
        TimeWindow.Since("", null, Now).Should().BeNull();
    }

    [Fact]
    public void Something_unreadable_is_refused_or_falls_back()
    {
        FluentActions.Invoking(() => TimeWindow.Since("soon", null, Now)).Should().Throw<FormatException>().WithMessage("*use 7d, 12h or 2026-10-01*");
        TimeWindow.SinceOrDefault("soon", TimeSpan.FromDays(30), Now).Should().Be(Now.AddDays(-30));
        TimeWindow.SinceOrDefault("7d", TimeSpan.FromDays(30), Now).Should().Be(Now.AddDays(-7));
    }
}
