using FluentAssertions;
using Plate.Core.Time;

namespace Plate.Core.Tests;

public class UserClockTests
{
    [Fact]
    public void Late_evening_utc_is_already_tomorrow_in_Sofia_summer_time() =>
        // 21:30 UTC is 00:30 EEST (UTC+3).
        UserClock.Today(new DateTimeOffset(2026, 10, 1, 21, 30, 0, TimeSpan.Zero), "Europe/Sofia")
            .Should().Be(new DateOnly(2026, 10, 2));

    [Fact]
    public void Late_evening_utc_is_already_tomorrow_in_Sofia_winter_time() =>
        // 22:30 UTC is 00:30 EET (UTC+2).
        UserClock.Today(new DateTimeOffset(2026, 12, 1, 22, 30, 0, TimeSpan.Zero), "Europe/Sofia")
            .Should().Be(new DateOnly(2026, 12, 2));

    [Fact]
    public void Before_local_midnight_stays_on_the_same_day() =>
        UserClock.Today(new DateTimeOffset(2026, 12, 1, 21, 59, 0, TimeSpan.Zero), "Europe/Sofia")
            .Should().Be(new DateOnly(2026, 12, 1));

    [Fact]
    public void Ignores_the_offset_of_the_input() =>
        UserClock.Today(new DateTimeOffset(2026, 10, 2, 0, 30, 0, TimeSpan.FromHours(3)), "Europe/Sofia")
            .Should().Be(new DateOnly(2026, 10, 2));
}
