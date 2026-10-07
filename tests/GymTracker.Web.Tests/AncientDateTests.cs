using System.Net;
using FluentAssertions;

namespace GymTracker.Web.Tests;

/// <summary>A day far outside anything the app logs, from a hand-typed URL or a link without a date.</summary>
[Collection(PostgresCollection.Name)]
public class AncientDateTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("/food?date=0001-01-01")]
    [InlineData("/food/add?date=0001-01-01&meal=Lunch&tab=manual")]
    public async Task An_ancient_day_goes_back_to_today_instead_of_failing(string path)
    {
        await using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        response.Headers.Location!.OriginalString.Should().Be("/food");
    }

    [Fact]
    public async Task Food_cannot_be_logged_for_an_ancient_day()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());

        await s.Invoking(x => x.FoodLog.AddQuickAsync(new DateOnly(1, 1, 1), GymTracker.Core.Domain.Meal.Lunch, "x", 100, 0, 0, 0, null, null, default))
            .Should().ThrowAsync<GymTracker.Web.Features.ValidationException>().WithMessage("That day is too far back.");
    }
}
