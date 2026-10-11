using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

/// <summary>Today's protein ring, the weight lines on Today and Progress, and error noise (Phase 11).</summary>
public class TodayProgressTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private async Task<(HttpClient Client, UserScope Scope)> WithTargetsAsync(AppFactory app)
    {
        var (client, email) = await app.SignedInClientAsync();
        await using var system = fixture.CreateContext(null);
        var userId = await system.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        await system.Profiles.IgnoreQueryFilters().Where(p => p.UserId == userId).ExecuteUpdateAsync(p => p
            .SetProperty(x => x.Sex, Sex.Male).SetProperty(x => x.BirthYear, 1990).SetProperty(x => x.HeightCm, 180));
        var s = new UserScope(fixture, userId);
        var today = UserClock.Today(DateTimeOffset.UtcNow, UserProfile.DefaultTimeZoneId);
        await s.Bodyweights.SetAsync(today.AddDays(-6), 81, default);
        await s.Bodyweights.SetAsync(today, 80, default);
        return (client, s);
    }

    [Fact]
    public async Task The_protein_ring_counts_to_the_aim_and_shows_the_minimum_under_it()
    {
        await using var app = new AppFactory(fixture);
        var (client, s) = await WithTargetsAsync(app);
        await using var _ = s;

        var html = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/today"));
        html.Should().Contain("protein left").And.NotContain("to your protein minimum");
        html.Should().MatchRegex(@"data-protein-min>min \d+ g");
        html.Should().Contain("Logged today: 80 kg").And.NotContain(", trend ");
    }

    [Fact]
    public async Task Progress_shows_how_the_trend_moved_instead_of_repeating_it()
    {
        await using var app = new AppFactory(fixture);
        var (client, s) = await WithTargetsAsync(app);
        await using var _ = s;

        var html = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/progress"));
        html.Should().MatchRegex(@"data-trend-change>[−+±][\d.]+ kg over this range").And.NotContain("Trend now:");
    }

    [Fact]
    public async Task A_skipped_page_transition_is_not_recorded_as_an_error()
    {
        await using var app = new AppFactory(fixture);
        var (client, email) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/today");
        await AppFactory.PostAsync(client, "/feedback/client-error", token, new()
        {
            ["message"] = "Transition was aborted because of invalid state. Document hidden", ["page"] = "/food",
        });

        await using var db = fixture.CreateContext(null);
        (await db.ErrorEvents.IgnoreQueryFilters().AnyAsync(e => e.Message.Contains("Transition was aborted"))).Should().BeFalse();
    }
}
