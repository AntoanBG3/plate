using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Tests;

public class PlatesEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    /// <summary>A signed-in client whose user has an active session with one exercise.</summary>
    private async Task<(HttpClient Client, int SessionExerciseId)> SessionWithAsync(AppFactory app, string exerciseName, decimal? loggedKg = null)
    {
        var (client, email) = await app.SignedInClientAsync();
        await using var system = fixture.CreateContext(null);
        var userId = await system.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        await using var s = new UserScope(fixture, userId);
        var exercise = await s.Db.Exercises.FirstAsync(e => e.Name == exerciseName);
        var template = await s.Templates.CreateAsync("Push", default);
        await s.Templates.AddExerciseAsync(template.Id, exercise.Id, default);
        var session = await s.Sessions.GetAsync((await s.Sessions.StartAsync(template.Id, default)).Id, default);
        if (loggedKg is { } kg)
        {
            await s.Sessions.LogSetAsync(new Plate.Web.Features.Training.LogSetInput(Guid.NewGuid(), session.Exercises[0].Id, kg, 8, null, false, null), default);
        }

        return (client, session.Exercises[0].Id);
    }

    [Fact]
    public async Task Plates_list_what_goes_on_each_side()
    {
        await using var app = new AppFactory(fixture);
        var (client, id) = await SessionWithAsync(app, "Bench press");

        var html = await client.GetStringAsync($"/train/session-exercises/{id}/plates?weight=100");

        html.Should().Contain("data-plate=\"25\"").And.Contain("data-plate=\"15\"").And.Contain("data-total=\"100\"");
    }

    [Fact]
    public async Task The_sheet_names_the_bar_and_gives_one_total()
    {
        await using var app = new AppFactory(fixture);
        var (client, id) = await SessionWithAsync(app, "Bench press");

        var exact = await client.GetStringAsync($"/train/session-exercises/{id}/plates?weight=100");
        exact.Should().Contain("Bar 20 kg").And.Contain("100 kg in total").And.NotContain("Closest you can load");

        // 103 can't be loaded: one line says what can, and what was asked for (QA: the two lines said the same thing).
        var closest = await client.GetStringAsync($"/train/session-exercises/{id}/plates?weight=103");
        closest.Should().Contain("Closest you can load: 102.5 kg (you asked for 103)").And.NotContain("kg in total");
    }

    [Fact]
    public async Task Plates_without_a_weight_asks_for_one()
    {
        await using var app = new AppFactory(fixture);
        var (client, id) = await SessionWithAsync(app, "Bench press");

        var response = await client.GetAsync($"/train/session-exercises/{id}/plates");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("data-plates-empty");
    }

    [Fact]
    public async Task Opening_the_sheet_starts_from_the_last_working_set()
    {
        await using var app = new AppFactory(fixture);
        var (client, id) = await SessionWithAsync(app, "Bench press", loggedKg: 80);

        (await client.GetStringAsync($"/train/session-exercises/{id}/plates")).Should().Contain("data-total=\"80\"");
    }

    [Theory]
    [InlineData("")]
    [InlineData(",")]
    [InlineData("1.2.")]
    public async Task An_incomplete_field_asks_for_a_weight_instead_of_falling_back(string weight)
    {
        await using var app = new AppFactory(fixture);
        var (client, id) = await SessionWithAsync(app, "Bench press", loggedKg: 80);

        var html = await client.GetStringAsync($"/train/session-exercises/{id}/plates?typed=true&weight={weight}");

        html.Should().Contain("data-plates-empty").And.NotContain("data-total");
    }

    [Fact]
    public async Task An_impossible_weight_asks_for_a_real_one()
    {
        await using var app = new AppFactory(fixture);
        var (client, id) = await SessionWithAsync(app, "Bench press");

        var html = await client.GetStringAsync($"/train/session-exercises/{id}/plates?weight=100000000");

        html.Should().Contain("data-plates-empty");
    }

    [Fact]
    public async Task An_exercise_without_a_bar_has_no_plates()
    {
        await using var app = new AppFactory(fixture);
        var (client, id) = await SessionWithAsync(app, "Leg press");

        (await client.GetAsync($"/train/session-exercises/{id}/plates?weight=100")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Another_users_session_exercise_is_not_found()
    {
        await using var app = new AppFactory(fixture);
        var (_, id) = await SessionWithAsync(app, "Bench press");
        var (stranger, _) = await app.SignedInClientAsync();

        (await stranger.GetAsync($"/train/session-exercises/{id}/plates?weight=100")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
