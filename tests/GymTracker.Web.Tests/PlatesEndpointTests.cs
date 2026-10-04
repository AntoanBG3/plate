using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class PlatesEndpointTests(PostgresFixture fixture)
{
    /// <summary>A signed-in client whose user has an active session with one exercise.</summary>
    private async Task<(HttpClient Client, int SessionExerciseId)> SessionWithAsync(AppFactory app, string exerciseName)
    {
        var (client, email) = await app.SignedInClientAsync();
        await using var system = fixture.CreateContext(null);
        var userId = await system.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        await using var s = new UserScope(fixture, userId);
        var exercise = await s.Db.Exercises.FirstAsync(e => e.Name == exerciseName);
        var template = await s.Templates.CreateAsync("Push", default);
        await s.Templates.AddExerciseAsync(template.Id, exercise.Id, default);
        var session = await s.Sessions.GetAsync((await s.Sessions.StartAsync(template.Id, default)).Id, default);
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
    public async Task Plates_without_a_weight_asks_for_one()
    {
        await using var app = new AppFactory(fixture);
        var (client, id) = await SessionWithAsync(app, "Bench press");

        var response = await client.GetAsync($"/train/session-exercises/{id}/plates");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("data-plates-empty");
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
