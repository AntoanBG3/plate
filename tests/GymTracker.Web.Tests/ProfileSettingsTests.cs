using System.Net;
using FluentAssertions;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class ProfileSettingsTests(PostgresFixture db) : IDisposable
{
    private readonly AppFactory app = new(db);

    public void Dispose() => app.Dispose();

    private static Dictionary<string, string> Form(string activity = "LowActive") => new()
    {
        ["displayName"] = "Test",
        ["sex"] = "Male",
        ["birthYear"] = "1996",
        ["heightCm"] = "180",
        ["activityLevel"] = activity,
        ["goal"] = "Maintain",
        ["timeZoneId"] = "Europe/Sofia",
        ["platesKg"] = "20",
    };

    [Fact]
    public async Task The_activity_level_is_saved_and_shown_checked()
    {
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/settings");

        var response = await AppFactory.PostAsync(client, "/settings/profile", token, Form(activity: "Active"));

        AppFactory.IsRedirectTo(response, "/settings").Should().BeTrue();
        var html = await client.GetStringAsync("/settings");
        html.Should().MatchRegex("name=\"activityLevel\" value=\"Active\" checked");
    }

    [Fact]
    public async Task Saved_on_change_it_answers_with_todays_protein_note_and_no_notice()
    {
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/settings");
        var form = Form();
        form["autosave"] = "true";
        form["heightCm"] = "175";

        var response = await AppFactory.PostAsync(client, "/settings/profile", token, form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("id=\"protein-note\" hx-swap-oob=\"true\"");
        var html = await client.GetStringAsync("/settings");
        html.Should().Contain("name=\"heightCm\" value=\"175\"").And.NotContain("Profile saved.");
    }

    [Theory]
    [InlineData("Cut")]
    [InlineData("Bulk")]
    public async Task A_cut_or_bulk_without_a_rate_is_refused(string goal)
    {
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/settings");
        var form = Form();
        form["goal"] = goal;

        var response = await AppFactory.PostAsync(client, "/settings/profile", token, form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Be("Enter a weekly rate for a cut or bulk.");
    }

    [Fact]
    public async Task The_time_zone_is_picked_from_a_list()
    {
        var (client, _) = await app.SignedInClientAsync();
        var html = await client.GetStringAsync("/settings");
        html.Should().MatchRegex("<select id=\"timeZoneId\" name=\"timeZoneId\">")
            .And.Contain("<option value=\"Europe/Sofia\" selected=\"selected\">Sofia</option>")
            .And.Contain("<optgroup label=\"America\">");
    }

    [Theory]
    [InlineData("birthYear", "2015")]      // 11 years old
    [InlineData("heightCm", "250")]
    [InlineData("displayName", "An unreasonably long display name of more than fifty characters")]
    public async Task Values_outside_the_limits_are_refused(string field, string value)
    {
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/settings");

        var form = Form();
        form[field] = value;
        var response = await AppFactory.PostAsync(client, "/settings/profile", token, form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Settings_inputs_take_their_ranges_from_the_limits()
    {
        var (client, _) = await app.SignedInClientAsync();

        var html = await client.GetStringAsync("/settings");

        var year = DateTime.UtcNow.Year;
        html.Should().Contain($"name=\"birthYear\" min=\"{year - 100}\" max=\"{year - 18}\"");
        html.Should().Contain("name=\"displayName\" value=\"Test\" maxlength=\"50\"");
    }
}
