using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Tests;

/// <summary>A typed comma is read as a decimal point: the Bulgarian iPhone keypad has no dot (SPEC 8).</summary>
public class DecimalCommaTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_weight_typed_with_a_comma_is_saved()
    {
        await using var app = new AppFactory(fixture);
        var (client, email) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/today");

        var response = await client.PostAsync("/today/weight", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["weightKg"] = "82,5",
            ["__RequestVerificationToken"] = token,
        }));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Redirect);
        await using var db = fixture.CreateContext(null);
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        (await db.Bodyweights.IgnoreQueryFilters().Where(b => b.UserId == userId).Select(b => b.WeightKg).SingleAsync()).Should().Be(82.5m);
    }

    [Theory]
    [InlineData("62,5", 62.5)]
    [InlineData("62.5", 62.5)]
    [InlineData(" 100 ", 100)]
    public async Task A_set_weight_reads_either_separator(string typed, decimal expected)
    {
        await using var app = new AppFactory(fixture);
        var (client, email) = await app.SignedInClientAsync();
        await using var system = fixture.CreateContext(null);
        var userId = await system.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        await using var s = new UserScope(fixture, userId);
        var template = await s.Templates.CreateAsync("Upper", default);
        await s.Templates.AddExerciseAsync(template.Id, (await s.Db.Exercises.FirstAsync(e => e.Name == "Bench press")).Id, default);
        var session = await s.Sessions.GetAsync((await s.Sessions.StartAsync(template.Id, default)).Id, default);
        var token = await AppFactory.TokenAsync(client, $"/train/sessions/{session.Id}");

        var response = await AppFactory.PostAsync(client, "/train/sets", token, new()
        {
            ["clientId"] = Guid.NewGuid().ToString(), ["sessionExerciseId"] = session.Exercises[0].Id.ToString(),
            ["weightKg"] = typed, ["reps"] = "8", ["isWarmup"] = "false",
        });

        response.IsSuccessStatusCode.Should().BeTrue();
        (await s.Db.Sets.AsNoTracking().SingleAsync()).WeightKg.Should().Be(expected);
    }
}
