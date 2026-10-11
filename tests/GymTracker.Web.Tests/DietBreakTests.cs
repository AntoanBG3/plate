using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

/// <summary>The cut start date, the diet break offer, and starting and ending a break (SPEC 5.9).</summary>
public class DietBreakTests(PostgresFixture db) : IDisposable, IClassFixture<PostgresFixture>
{
    private readonly AppFactory app = new(db);

    public void Dispose() => app.Dispose();

    private static Dictionary<string, string> Form(string goal) => new()
    {
        ["activityLevel"] = "Inactive", ["goal"] = goal, ["targetRatePercentPerWeek"] = goal == "Maintain" ? "0" : "0.5",
    };

    private async Task<UserProfile> ProfileAsync(string email)
    {
        await using var ctx = db.CreateContext(null);
        var id = await ctx.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        return await ctx.Profiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.UserId == id);
    }

    private async Task SetCutStartAsync(string email, DateOnly started)
    {
        await using var ctx = db.CreateContext(null);
        var id = await ctx.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        await ctx.Profiles.IgnoreQueryFilters().Where(p => p.UserId == id).ExecuteUpdateAsync(u => u.SetProperty(p => p.CutStartedOn, started));
    }

    [Fact]
    public async Task A_cut_offers_a_break_after_12_weeks_which_pauses_it_until_it_ends()
    {
        var (client, email) = await app.SignedInClientAsync();
        var today = UserClock.Today(DateTimeOffset.UtcNow, "Europe/Sofia");

        await AppFactory.PostAsync(client, "/settings/goal", await AppFactory.TokenAsync(client, "/settings/goal"), Form("Cut"));
        (await ProfileAsync(email)).CutStartedOn.Should().Be(today);
        (await client.GetStringAsync("/today")).Should().NotContain("Take a 2-week break");

        await SetCutStartAsync(email, today.AddDays(-84));
        (await client.GetStringAsync("/today")).Should().Contain("Take a 2-week break");

        var response = await AppFactory.PostAsync(client, "/today/cut/break", await AppFactory.TokenAsync(client, "/today"));
        AppFactory.IsRedirectTo(response, "/today").Should().BeTrue();
        (await ProfileAsync(email)).DietBreakUntil.Should().Be(today.AddDays(13));
        (await client.GetStringAsync("/today")).Should().Contain("Diet break until");

        await AppFactory.PostAsync(client, "/today/cut/end-break", await AppFactory.TokenAsync(client, "/today"));
        var ended = await ProfileAsync(email);
        (ended.DietBreakUntil, ended.CutStartedOn).Should().Be(((DateOnly?)null, today));

        await AppFactory.PostAsync(client, "/settings/goal", await AppFactory.TokenAsync(client, "/settings/goal"), Form("Maintain"));
        (await ProfileAsync(email)).CutStartedOn.Should().BeNull();
    }

    [Fact]
    public async Task Only_a_cut_can_take_a_break()
    {
        var (client, email) = await app.SignedInClientAsync();
        await AppFactory.PostAsync(client, "/settings/goal", await AppFactory.TokenAsync(client, "/settings/goal"), Form("Maintain"));

        await AppFactory.PostAsync(client, "/today/cut/break", await AppFactory.TokenAsync(client, "/today"));

        (await ProfileAsync(email)).DietBreakUntil.Should().BeNull();
    }
}
