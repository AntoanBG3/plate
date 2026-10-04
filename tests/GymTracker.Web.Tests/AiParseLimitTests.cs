using System.Net;
using FluentAssertions;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class AiParseLimitTests(PostgresFixture db)
{
    [Fact]
    public async Task Food_text_parsing_is_rate_limited_per_user()
    {
        // Each parse can spend the shared OpenRouter balance; a stuck client or stolen session must not loop it.
        using var app = new AppFactory(db, settings: new Dictionary<string, string?> { ["RateLimits:AiParsePerWindow"] = "2" });
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/food/add");
        var fields = new Dictionary<string, string> { ["date"] = "2026-10-04", ["meal"] = "Lunch", ["text"] = "a banana" };

        (await AppFactory.PostAsync(client, "/food/parse", token, fields)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AppFactory.PostAsync(client, "/food/parse", token, fields)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await AppFactory.PostAsync(client, "/food/parse", token, fields)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }
}
