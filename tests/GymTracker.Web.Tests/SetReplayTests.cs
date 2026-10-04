using FluentAssertions;
using GymTracker.Web.Features.Training;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

/// <summary>A set retried from the phone's queue after it was deleted must not come back as a logged row.</summary>
[Collection(PostgresCollection.Name)]
public class SetReplayTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Replaying_a_deleted_set_returns_an_empty_row()
    {
        await using var app = new AppFactory(fixture);
        var (client, email) = await app.SignedInClientAsync();
        await using var system = fixture.CreateContext(null);
        var userId = await system.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        await using var s = new UserScope(fixture, userId);
        var bench = await s.Db.Exercises.FirstAsync(e => e.Name == "Bench press");
        var template = await s.Templates.CreateAsync("Push", default);
        await s.Templates.AddExerciseAsync(template.Id, bench.Id, default);
        var session = await s.Sessions.GetAsync((await s.Sessions.StartAsync(template.Id, default)).Id, default);
        var input = new LogSetInput(Guid.NewGuid(), session.Exercises[0].Id, 80, 8, null, false, null);
        var set = await s.Sessions.LogSetAsync(input, default);
        await s.Sessions.DeleteSetAsync(set.Id, default);

        var token = await AppFactory.TokenAsync(client, $"/train/sessions/{session.Id}");
        var response = await AppFactory.PostAsync(client, "/train/sets", token, new Dictionary<string, string>
        {
            ["clientId"] = input.ClientId.ToString(),
            ["sessionExerciseId"] = input.SessionExerciseId.ToString(),
            ["weightKg"] = "80",
            ["reps"] = "8",
            ["isWarmup"] = "false",
        });

        response.IsSuccessStatusCode.Should().BeTrue();
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("data-log-set").And.NotContain("set-row done");
        html.Should().NotContain(input.ClientId.ToString(), "a fresh row gets a new client id");
    }
}
