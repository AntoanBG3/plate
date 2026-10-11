using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

/// <summary>The Food totals card reads the same way in every cell (Phase 11).</summary>
public class FoodTotalsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Each_cell_has_one_word_and_its_target_in_one_line()
    {
        await using var app = new AppFactory(fixture);
        var (client, email) = await app.SignedInClientAsync();
        await using var system = fixture.CreateContext(null);
        var userId = await system.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        await system.Profiles.IgnoreQueryFilters().Where(p => p.UserId == userId).ExecuteUpdateAsync(p => p
            .SetProperty(x => x.Sex, Sex.Male).SetProperty(x => x.BirthYear, 1990).SetProperty(x => x.HeightCm, 180));
        await using var s = new UserScope(fixture, userId);
        var today = UserClock.Today(DateTimeOffset.UtcNow, UserProfile.DefaultTimeZoneId);
        await s.Bodyweights.SetAsync(today, 80, default);
        // A food with no fiber or saturated fat value: the totals become lower bounds.
        await s.FoodLog.AddQuickAsync(today, Meal.Lunch, "Wrap", 500, 30, 50, 15, null, null, default);

        var page = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/food"));
        page.Should().MatchRegex(@"aim \d+ · min \d+").And.MatchRegex(@"min \d+</div>").And.MatchRegex(@"max \d+</div>");
        page.Should().NotContain("protein of").And.NotContain("fiber of").And.NotContain(">saturated fat, up to", "the meter keeps it in its accessible label only");
        page.Should().NotContain("At least:").And.NotContain("Incomplete:").And.Contain("Some foods have no fiber or saturated fat value");
        page.Should().NotContain("— <small>", "a missing value has no unit");
    }
}
