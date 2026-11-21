using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Plate.Core.Domain;
using Plate.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Tests;

public class FoodRequestTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly AppFactory app = new(fixture);
    private static DateOnly Today => UserClock.Today(DateTimeOffset.UtcNow, UserProfile.DefaultTimeZoneId);
    public void Dispose() => app.Dispose();

    [Theory]
    [InlineData("quick")]
    [InlineData("log-item")]
    [InlineData("log-recent")]
    [InlineData("items")]
    [InlineData("confirm")]
    [InlineData("copy")]
    [InlineData("saved-meal")]
    public async Task Resending_one_food_form_returns_its_first_result_without_adding_entries(string kind)
    {
        var (client, email) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/food");
        await using var db = fixture.CreateContext(null);
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        var foodId = await db.FoodItems.IgnoreQueryFilters().Where(f => f.OwnerUserId == null).Select(f => f.Id).FirstAsync();
        var fields = new Dictionary<string, string>
        {
            ["date"] = Today.ToString("yyyy-MM-dd"), ["meal"] = "Dinner",
            ["requestId"] = Guid.NewGuid().ToString(), ["description"] = "Request test", ["grams"] = "100",
            ["kcal"] = "100", ["protein"] = "10", ["carbs"] = "10", ["fat"] = "2",
        };
        var path = "/food/" + kind;
        switch (kind)
        {
            case "log-item":
                fields["foodItemId"] = foodId.ToString();
                fields["source"] = "Search";
                break;
            case "items":
                fields["name"] = "Private request food";
                break;
            case "confirm":
                var parse = await AppFactory.PostAsync(client, "/food/parse", token,
                    new() { ["date"] = fields["date"], ["meal"] = "Dinner", ["text"] = "100g banana" });
                var html = await parse.Content.ReadAsStringAsync();
                fields["parseRunId"] = Regex.Match(html, "name=\"parseRunId\" value=\"(\\d+)\"").Groups[1].Value;
                fields["parseRunId"].Should().NotBeEmpty();
                foreach (var (key, value) in new Dictionary<string, string>
                {
                    ["Include"] = "true", ["Description"] = "Request test", ["Grams"] = "100",
                    ["Kcal"] = "100", ["Protein"] = "10", ["Carbs"] = "10", ["Fat"] = "2",
                }) fields["items[0]." + key] = value;
                break;
            case "copy":
            case "saved-meal":
                (await AppFactory.PostAsync(client, "/food/quick", token, new()
                {
                    ["date"] = (kind == "copy" ? Today.AddDays(-1) : Today).ToString("yyyy-MM-dd"),
                    ["meal"] = "Dinner", ["description"] = "Seed request food", ["kcal"] = "100",
                    ["requestId"] = Guid.NewGuid().ToString(),
                })).StatusCode.Should().Be(HttpStatusCode.Redirect);
                if (kind == "saved-meal")
                {
                    (await AppFactory.PostAsync(client, "/food/saved-meals", token,
                        new() { ["date"] = fields["date"], ["meal"] = "Dinner", ["name"] = "Request meal" }))
                        .StatusCode.Should().Be(HttpStatusCode.Redirect);
                    var id = await db.SavedMeals.IgnoreQueryFilters().Where(m => m.UserId == userId).Select(m => m.Id).SingleAsync();
                    path = $"/food/saved-meals/{id}/log";
                    fields["meal"] = "Lunch";
                }
                break;
        }

        var first = await AppFactory.PostAsync(client, path, token, fields);
        if (kind == "confirm") fields["items[0].Grams"] = "250";
        var second = await AppFactory.PostAsync(client, path, token, fields);
        first.StatusCode.Should().Be(HttpStatusCode.Redirect);
        second.StatusCode.Should().Be(first.StatusCode);
        second.Headers.Location.Should().Be(first.Headers.Location);
        if (kind == "confirm")
        {
            var page = await client.GetStringAsync(second.Headers.Location!);
            page.Should().Contain("This review was already saved.");
        }
        var meal = Enum.Parse<Meal>(fields["meal"]);
        (await db.FoodLog.IgnoreQueryFilters().CountAsync(x => x.UserId == userId && x.Date == Today && x.Meal == meal))
            .Should().Be(1, "one rendered form is one intent even if its response is lost");
        if (kind == "items")
            (await db.FoodItems.IgnoreQueryFilters().CountAsync(f => f.OwnerUserId == userId && f.Name == "Private request food")).Should().Be(1);
    }


    [Fact]
    public async Task Simultaneous_resends_commit_one_food_and_one_receipt()
    {
        var (client, email) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/food");
        var fields = QuickFields();
        var responses = await Task.WhenAll(
            AppFactory.PostAsync(client, "/food/quick", token, fields),
            AppFactory.PostAsync(client, "/food/quick", token, fields));
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Redirect);
        responses[0].Headers.Location.Should().Be(responses[1].Headers.Location);
        await using var db = fixture.CreateContext(null);
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        (await db.FoodLog.IgnoreQueryFilters().CountAsync(x => x.UserId == userId)).Should().Be(1);
        (await db.FoodLogRequests.IgnoreQueryFilters().CountAsync(x => x.UserId == userId)).Should().Be(1);
    }

    [Fact]
    public async Task A_successful_intent_keeps_its_first_values_but_a_new_intent_can_log_again()
    {
        var (client, email) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/food");
        var fields = QuickFields();
        var first = await AppFactory.PostAsync(client, "/food/quick", token, fields);
        fields["kcal"] = "999";
        fields["date"] = Today.AddDays(-1).ToString("yyyy-MM-dd");
        var retry = await AppFactory.PostAsync(client, "/food/quick", token, fields);
        retry.Headers.Location.Should().Be(first.Headers.Location);
        await using var db = fixture.CreateContext(null);
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        var saved = await db.FoodLog.IgnoreQueryFilters().SingleAsync(x => x.UserId == userId);
        saved.Kcal.Should().Be(100);
        saved.Date.Should().Be(Today);
        fields["requestId"] = Guid.NewGuid().ToString();
        (await AppFactory.PostAsync(client, "/food/quick", token, fields)).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await db.FoodLog.IgnoreQueryFilters().CountAsync(x => x.UserId == userId)).Should().Be(2);
    }

    [Fact]
    public async Task Validation_rolls_back_the_claim_so_the_same_form_can_be_corrected()
    {
        var (client, email) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/food");
        var fields = QuickFields();
        fields["kcal"] = "-1";
        (await AppFactory.PostAsync(client, "/food/quick", token, fields)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using var db = fixture.CreateContext(null);
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        (await db.FoodLogRequests.IgnoreQueryFilters().AnyAsync(x => x.UserId == userId)).Should().BeFalse();
        (await db.FoodLog.IgnoreQueryFilters().AnyAsync(x => x.UserId == userId)).Should().BeFalse();
        fields["kcal"] = "100";
        (await AppFactory.PostAsync(client, "/food/quick", token, fields)).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await db.FoodLog.IgnoreQueryFilters().CountAsync(x => x.UserId == userId)).Should().Be(1);
    }

    [Fact]
    public async Task A_missing_request_id_is_rejected_before_food_is_added()
    {
        var (client, email) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/food");
        var fields = QuickFields();
        fields.Remove("requestId");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/food/quick") { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("HX-Request", "true");
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("reload");
        await using var db = fixture.CreateContext(null);
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        (await db.FoodLog.IgnoreQueryFilters().AnyAsync(x => x.UserId == userId)).Should().BeFalse();
    }

    [Fact]
    public async Task Receipts_and_their_export_are_private_to_the_account()
    {
        var (first, firstEmail) = await app.SignedInClientAsync();
        var (second, secondEmail) = await app.SignedInClientAsync();
        var a = QuickFields();
        var b = QuickFields();
        (await AppFactory.PostAsync(first, "/food/quick", await AppFactory.TokenAsync(first, "/food"), a))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await AppFactory.PostAsync(second, "/food/quick", await AppFactory.TokenAsync(second, "/food"), b))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);
        await using var admin = fixture.CreateContext(null);
        var firstId = await admin.Users.Where(u => u.Email == firstEmail).Select(u => u.Id).SingleAsync();
        var secondId = await admin.Users.Where(u => u.Email == secondEmail).Select(u => u.Id).SingleAsync();
        await using var mine = fixture.CreateContext(firstId);
        (await mine.FoodLogRequests.Select(x => x.UserId).ToListAsync()).Should().Equal(firstId);
        await using var theirs = fixture.CreateContext(secondId);
        (await theirs.FoodLogRequests.CountAsync()).Should().Be(1);
        var files = ExportTests.Unzip(await new Plate.Web.Features.Export.DataExport(mine).ZipAsync(default));
        files["food_requests.csv"].Should().Contain(a["requestId"]).And.NotContain(b["requestId"]);
    }

    [Fact]
    public async Task Optional_usual_food_failure_does_not_undo_the_food_or_receipt()
    {
        var (client, email) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/food");
        var parsed = await AppFactory.PostAsync(client, "/food/parse", token,
            new() { ["date"] = Today.ToString("yyyy-MM-dd"), ["meal"] = "Dinner", ["text"] = "100g banana" });
        var run = Regex.Match(await parsed.Content.ReadAsStringAsync(), "name=\"parseRunId\" value=\"(\\d+)\"").Groups[1].Value;
        run.Should().NotBeEmpty();
        await using var db = fixture.CreateContext(null);
        var foodId = await db.FoodItems.IgnoreQueryFilters().Where(f => f.OwnerUserId == null).Select(f => f.Id).FirstAsync();
        var fields = QuickFields();
        fields["parseRunId"] = run;
        fields["items[0].Include"] = "true";
        fields["items[0].Description"] = "Banana";
        fields["items[0].Grams"] = "100";
        fields["items[0].Kcal"] = "100";
        fields["items[0].Protein"] = "10";
        fields["items[0].Carbs"] = "10";
        fields["items[0].Fat"] = "2";
        fields["items[0].FoodItemId"] = foodId.ToString();
        await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "UsualFoods" RENAME TO "UsualFoodsHeldForRequestTest";""");
        try
        {
            (await AppFactory.PostAsync(client, "/food/confirm", token, fields)).StatusCode.Should().Be(HttpStatusCode.Redirect);
            var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
            (await db.FoodLog.IgnoreQueryFilters().CountAsync(x => x.UserId == userId)).Should().Be(1);
            (await db.FoodLogRequests.IgnoreQueryFilters().CountAsync(x => x.UserId == userId)).Should().Be(1);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "UsualFoodsHeldForRequestTest" RENAME TO "UsualFoods";""");
        }
    }

    [Fact]
    public async Task A_multi_item_confirmation_is_atomic_and_its_resend_keeps_the_first_entries()
    {
        var (client, email) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/food");
        var fields = QuickFields();
        for (var i = 0; i < 2; i++)
        {
            fields[$"items[{i}].Include"] = "true";
            fields[$"items[{i}].Description"] = $"Atomic food {i}";
            fields[$"items[{i}].Grams"] = "100";
            fields[$"items[{i}].Kcal"] = "100";
        }
        fields["items[1].Grams"] = "-1";
        (await AppFactory.PostAsync(client, "/food/confirm", token, fields)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using var db = fixture.CreateContext(null);
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        (await db.FoodLog.IgnoreQueryFilters().AnyAsync(x => x.UserId == userId)).Should().BeFalse();
        (await db.FoodLogRequests.IgnoreQueryFilters().AnyAsync(x => x.UserId == userId)).Should().BeFalse();
        fields["items[1].Grams"] = "100";
        fields["items[1].Fat"] = "";
        (await AppFactory.PostAsync(client, "/food/confirm", token, fields)).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await AppFactory.PostAsync(client, "/food/confirm", token, fields)).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await db.FoodLog.IgnoreQueryFilters().Where(x => x.UserId == userId).Select(x => x.Kcal).ToListAsync())
            .Should().Equal(100, 100);
    }

    [Theory]
    [InlineData("date")]
    [InlineData("labelScanId")]
    public async Task A_malformed_hidden_manual_field_asks_to_reload(string field)
    {
        var (client, _) = await app.SignedInClientAsync();
        var fields = QuickFields();
        fields["name"] = "Rice";
        fields["grams"] = "100";
        fields[field] = "oops";
        var response = await AppFactory.PostAsync(client, "/food/items", await AppFactory.TokenAsync(client, "/food"), fields);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("reload").And.NotContain("valid number");
    }

    private static Dictionary<string, string> QuickFields() => new()
    {
        ["date"] = Today.ToString("yyyy-MM-dd"), ["meal"] = "Dinner", ["description"] = "Request intent",
        ["kcal"] = "100", ["requestId"] = Guid.NewGuid().ToString(),
    };

    [Fact]
    public async Task Each_rendered_food_add_form_has_a_fresh_request_id()
    {
        var (client, _) = await app.SignedInClientAsync();
        var ids = new List<string>();
        foreach (var path in new[] { "/food/quick?meal=Lunch", "/food/quick?meal=Lunch", "/food/add?tab=manual" })
        {
            var html = await client.GetStringAsync(path);
            var matches = Regex.Matches(html, "name=\"requestId\"[^>]*value=\"([^\"]+)\"");
            matches.Count.Should().BeGreaterThan(0, path);
            ids.AddRange(matches.Select(m => m.Groups[1].Value));
        }
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().OnlyContain(value => Guid.Parse(value) != Guid.Empty);
    }
}
