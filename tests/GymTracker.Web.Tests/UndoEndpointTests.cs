using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace GymTracker.Web.Tests;

public class UndoEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static FormUrlEncodedContent Form(string token, params (string Name, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)).Append(new("__RequestVerificationToken", token)));

    /// <summary>Signs in, quick-adds "Wrap" to 4 October and returns the entry id.</summary>
    private static async Task<(HttpClient Client, string Token, string Id)> WithEntryAsync(AppFactory app)
    {
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/food");
        await client.PostAsync("/food/quick", Form(token, ("date", "2026-10-04"), ("meal", "Lunch"), ("description", "Wrap"), ("kcal", "500")));
        var day = await client.GetStringAsync("/food?date=2026-10-04");
        return (client, token, Regex.Match(day, "/food/entries/(\\d+)").Groups[1].Value);
    }

    [Fact]
    public async Task Deleting_an_entry_offers_undo_and_undo_brings_it_back()
    {
        await using var app = new AppFactory(fixture);
        var (client, token, id) = await WithEntryAsync(app);

        var deleted = await client.PostAsync($"/food/entries/{id}/delete", Form(token));
        deleted.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var after = await client.GetStringAsync(deleted.Headers.Location!.ToString());
        after.Should().Contain($"data-undo-url=\"/undo/entry/{id}\"").And.NotContain("Wrap</span>");

        var undone = await client.PostAsync($"/undo/entry/{id}", Form(token, ("returnUrl", "/food?date=2026-10-04")));
        undone.Headers.Location!.ToString().Should().Be("/food?date=2026-10-04");
        (await client.GetStringAsync("/food?date=2026-10-04")).Should().Contain("Wrap</span>");
    }

    [Fact]
    public async Task Unknown_kinds_are_not_found()
    {
        await using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/food");

        (await client.PostAsync("/undo/session/1", Form(token, ("returnUrl", "/food")))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_outside_return_url_falls_back_to_today()
    {
        await using var app = new AppFactory(fixture);
        var (client, token, id) = await WithEntryAsync(app);
        await client.PostAsync($"/food/entries/{id}/delete", Form(token));

        var undone = await client.PostAsync($"/undo/entry/{id}", Form(token, ("returnUrl", "https://evil.example/")));

        undone.Headers.Location!.ToString().Should().Be("/today");
    }
}
