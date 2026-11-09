using System.Net;
using FluentAssertions;
using Plate.Web.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Plate.Web.Tests;

/// <summary>For three days after the move to plate.peychev.uk the sign-in page says passkeys need setting up again.</summary>
public class MoveNoticeTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private AppFactory AppAt(DateTimeOffset now) => new(fixture, services: s => s.AddSingleton<TimeProvider>(new Clock(now)));

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(60 * 24, false)]
    public async Task The_sign_in_page_explains_the_move_until_the_notice_ends(int minutesAfterEnd, bool shown)
    {
        await using var app = AppAt(Brand.MoveNoticeUntil.AddMinutes(minutesAfterEnd));

        var html = await app.NewClient().GetStringAsync("/account/login");

        html.Contains("data-move-notice").Should().Be(shown);
    }

    // A phone set to Bulgarian has no language cookie on the new host yet; the sign-in page follows the phone.
    [Fact]
    public async Task The_notice_is_in_Bulgarian_on_a_Bulgarian_phone_without_cookies()
    {
        await using var app = AppAt(Brand.MoveNoticeUntil.AddDays(-2));
        var client = app.NewClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("bg-BG");

        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/account/login"));

        html.Should().Contain("Plate се премести на plate.peychev.uk").And.Contain("Вход и сигурност");
    }
}
