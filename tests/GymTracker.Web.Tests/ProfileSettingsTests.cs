using System.Net;
using FluentAssertions;
using GymTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class ProfileSettingsTests(PostgresFixture db) : IDisposable
{
    private readonly AppFactory app = new(db);

    public void Dispose() => app.Dispose();

    private static Dictionary<string, string> Form() => new()
    {
        ["displayName"] = "Test",
        ["sex"] = "Male",
        ["birthYear"] = "1996",
        ["heightCm"] = "180",
    };

    private static Dictionary<string, string> GoalForm(string activity = "LowActive") => new()
    {
        ["activityLevel"] = activity,
        ["goal"] = "Maintain",
    };

    [Fact]
    public async Task The_activity_level_is_saved_and_shown_checked()
    {
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/settings/goal");

        var response = await AppFactory.PostAsync(client, "/settings/goal", token, GoalForm(activity: "Active"));

        AppFactory.IsRedirectTo(response, "/settings/goal").Should().BeTrue();
        var html = await client.GetStringAsync("/settings/goal");
        html.Should().MatchRegex("name=\"activityLevel\" value=\"Active\" checked");
    }

    [Fact]
    public async Task Saved_on_change_the_goal_answers_with_todays_protein_note_and_no_notice()
    {
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/settings/goal");
        var form = GoalForm();
        form["autosave"] = "true";
        form["proteinGramsPerKg"] = "2";

        var response = await AppFactory.PostAsync(client, "/settings/goal", token, form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("id=\"protein-note\" hx-swap-oob=\"true\"");
        var html = await client.GetStringAsync("/settings/goal");
        html.Should().Contain("name=\"proteinGramsPerKg\" value=\"2\"").And.NotContain("Profile saved.");
    }

    [Fact]
    public async Task Saved_on_change_the_profile_answers_with_nothing()
    {
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/settings/profile");
        var form = Form();
        form["autosave"] = "true";
        form["heightCm"] = "175";

        var response = await AppFactory.PostAsync(client, "/settings/profile", token, form);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetStringAsync("/settings/profile")).Should().Contain("name=\"heightCm\" value=\"175\"").And.NotContain("Profile saved.");
    }

    // Each page posts only its own fields; the others must keep their values.
    [Fact]
    public async Task Saving_one_page_leaves_the_fields_of_the_others_alone()
    {
        var (client, email) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/settings");
        await AppFactory.PostAsync(client, "/settings/goal", token, new()
        {
            ["activityLevel"] = "Active", ["goal"] = "Cut", ["targetRatePercentPerWeek"] = "0.5", ["proteinGramsPerKg"] = "2.2", ["initialMaintenanceKcal"] = "2600",
        });
        await AppFactory.PostAsync(client, "/settings/training", token, new() { ["defaultWeightIncrementKg"] = "5", ["barWeightKg"] = "15", ["platesKg"] = "10" });
        await AppFactory.PostAsync(client, "/settings/time-zone", token, new() { ["timeZoneId"] = "Europe/London" });
        var profile = Form();
        profile["heightCm"] = "172";
        AppFactory.IsRedirectTo(await AppFactory.PostAsync(client, "/settings/profile", token, profile), "/settings/profile").Should().BeTrue();

        await using var ctx = db.CreateContext(null);
        var id = await ctx.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        var saved = await ctx.Profiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.UserId == id);
        (saved.HeightCm, saved.ActivityLevel, saved.Goal, saved.TargetRatePercentPerWeek, saved.ProteinGramsPerKg, saved.InitialMaintenanceKcal)
            .Should().Be((172, ActivityLevel.Active, Goal.Cut, 0.5m, 2.2m, 2600));
        (saved.DefaultWeightIncrementKg, saved.BarWeightKg, saved.TimeZoneId).Should().Be((5m, 15m, "Europe/London"));
        saved.PlatesKg.Should().Equal(10m);
    }

    // The hub is a list of pages, each row saying what is set now (SPEC 7).
    [Fact]
    public async Task The_hub_links_each_page_with_what_is_set_now()
    {
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/settings");
        await AppFactory.PostAsync(client, "/settings/goal", token, new() { ["activityLevel"] = "LowActive", ["goal"] = "Cut", ["targetRatePercentPerWeek"] = "0.5" });

        var html = await client.GetStringAsync("/settings");

        foreach (var page in new[] { "profile", "goal", "training", "exercises", "general", "security" })
        {
            html.Should().Contain($"href=\"/settings/{page}\"");
            (await client.GetAsync($"/settings/{page}")).StatusCode.Should().Be(HttpStatusCode.OK, page);
        }

        WebUtility.HtmlDecode(html).Should().Contain("Cut · 0.5% a week · Low active").And.Contain("Bar 20 kg · Jump 2.5 kg").And.Contain("Password only");
        html.Should().NotContain("name=\"heightCm\"", "the fields are on their own pages");
    }

    [Theory]
    [InlineData("Cut")]
    [InlineData("Bulk")]
    public async Task A_cut_or_bulk_without_a_rate_is_refused(string goal)
    {
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/settings/goal");
        var form = GoalForm();
        form["goal"] = goal;

        var response = await AppFactory.PostAsync(client, "/settings/goal", token, form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Be("Enter a weekly rate for a cut or bulk.");
    }

    [Fact]
    public async Task The_time_zone_is_picked_from_a_list()
    {
        var (client, _) = await app.SignedInClientAsync();
        var html = await client.GetStringAsync("/settings/general");
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

        var html = await client.GetStringAsync("/settings/profile");

        var year = DateTime.UtcNow.Year;
        html.Should().Contain($"name=\"birthYear\" min=\"{year - 100}\" max=\"{year - 18}\"");
        html.Should().Contain("name=\"displayName\" value=\"Test\" maxlength=\"50\"");
    }
}
