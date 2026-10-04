using FluentAssertions;
using GymTracker.Web.Features;
using GymTracker.Web.Features.Training;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class SwapExerciseTests(PostgresFixture fixture)
{
    private async Task<(UserScope S, int TemplateId, int SessionExerciseId, int InclineId)> StartedAsync()
    {
        var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var bench = await s.Db.Exercises.FirstAsync(e => e.Name == "Bench press");
        var incline = await s.Db.Exercises.FirstAsync(e => e.Name == "Incline dumbbell press");
        var template = await s.Templates.CreateAsync("Push", default);
        await s.Templates.AddExerciseAsync(template.Id, bench.Id, default);
        var item = (await s.Templates.GetAsync(template.Id, default)).Exercises[0];
        await s.Templates.UpdateExerciseAsync(item.Id, new TemplateExerciseInput(4, 6, 10, 2.5m, 150), default);
        var session = await s.Sessions.GetAsync((await s.Sessions.StartAsync(template.Id, default)).Id, default);
        return (s, template.Id, session.Exercises[0].Id, incline.Id);
    }

    [Fact]
    public async Task Swapping_keeps_the_slot_and_leaves_the_template_alone()
    {
        var (s, templateId, seId, inclineId) = await StartedAsync();
        await using var _ = s;

        await s.Sessions.SwapExerciseAsync(seId, inclineId, default);

        s.Db.ChangeTracker.Clear();
        var item = await s.Sessions.GetExerciseAsync(seId, default);
        (item.ExerciseId, item.WorkingSets, item.RepMin, item.RepMax, item.RestSeconds).Should().Be((inclineId, 4, 6, 10, 150));
        (await s.Templates.GetAsync(templateId, default)).Exercises[0].Exercise.Name.Should().Be("Bench press");
    }

    [Fact]
    public async Task Swapping_is_refused_once_a_set_is_logged()
    {
        var (s, _, seId, inclineId) = await StartedAsync();
        await using var _ = s;
        await s.Sessions.LogSetAsync(new LogSetInput(Guid.NewGuid(), seId, 40, 10, null, true, null), default);

        await s.Invoking(x => x.Sessions.SwapExerciseAsync(seId, inclineId, default)).Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Another_users_session_exercise_is_not_found()
    {
        var (s, _, seId, inclineId) = await StartedAsync();
        await using var _ = s;
        await using var other = new UserScope(fixture, await fixture.CreateUserAsync());

        await other.Invoking(x => x.Sessions.SwapExerciseAsync(seId, inclineId, default)).Should().ThrowAsync<NotFoundException>();
    }
}
