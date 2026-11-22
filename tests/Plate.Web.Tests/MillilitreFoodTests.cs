using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Plate.Core.Domain;
using Plate.Core.Nutrition;
using Plate.Web.Features.Export;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plate.Web.Tests;

/// <summary>Foods from a per-100 ml label keep their ml basis: their amounts are entered and shown in ml (SPEC 4, 6.6).</summary>
public class MillilitreFoodTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly LabelReading Ayran = new(true, "Айрян", "Верея", LabelBasis.Per100ml, 250, 38, null, 1.8m, 2.6m, 2.2m, 1.4m, null);
    private const string AddPage = "/food/add?date=2026-10-05&meal=Lunch&tab=manual";

    private (AppFactory App, FakeLabelReader Reader) NewApp()
    {
        var reader = new FakeLabelReader { Next = new(Ayran, "{}", null, "test/label") };
        var app = new AppFactory(fixture, services: s => s
            .AddScoped<ILabelReader>(_ => reader)
            .AddSingleton<IFoodDatabase, FakeFoodDatabase>());
        return (app, reader);
    }

    private static async Task<string> ReadLabelAsync(HttpClient client, string token)
    {
        var photo = new byte[5000];
        (photo[0], photo[1], photo[2]) = (0xFF, 0xD8, 0xFF);
        var file = new ByteArrayContent(photo);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var form = new MultipartFormDataContent
        {
            { new StringContent("2026-10-05"), "date" },
            { new StringContent("Lunch"), "meal" },
            { new StringContent("true"), "barcodeField" },
            { file, "photo", "label.jpg" },
            { new StringContent("1200"), "width" },
            { new StringContent("900"), "height" },
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "/food/label") { Content = form };
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("HX-Request", "true");
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, string token, string name, string unit, int? scanId = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["date"] = "2026-10-05", ["meal"] = "Lunch", ["name"] = name, ["unit"] = unit,
            ["kcal"] = "38", ["protein"] = "1.8", ["carbs"] = "2.6", ["fat"] = "2.2", ["saturatedFat"] = "1.4",
            ["servingGrams"] = "250", ["grams"] = "250", ["requestId"] = Guid.NewGuid().ToString(), ["__RequestVerificationToken"] = token,
        };
        if (scanId is { } id) fields["labelScanId"] = id.ToString();
        return client.PostAsync("/food/items", new FormUrlEncodedContent(fields));
    }

    [Fact]
    public async Task A_per_100_ml_label_fills_the_form_in_ml()
    {
        var (app, _) = NewApp();
        await using var _app = app;
        var (client, _) = await app.SignedInClientAsync();
        var html = await ReadLabelAsync(client, await AppFactory.TokenAsync(client, AddPage));

        html.Should().MatchRegex("<input type=\"radio\" name=\"unit\" value=\"Millilitres\" checked=\"checked\"");
        html.Should().NotMatchRegex("<input type=\"radio\" name=\"unit\" value=\"Grams\" checked");
        html.Should().MatchRegex("id=\"m-kcal\"[^>]*value=\"38\"", "the values stay per 100 ml");
        html.Should().Contain("The label gives values per 100 ml, so amounts of this food are in ml.");
        html.Should().NotContain("used as per 100 g");
    }

    [Fact]
    public async Task A_food_saved_in_ml_is_logged_and_shown_in_ml()
    {
        var (app, _) = NewApp();
        await using var _app = app;
        var (client, email) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, AddPage);
        var scanId = int.Parse(System.Text.RegularExpressions.Regex.Match(await ReadLabelAsync(client, token), "name=\"labelScanId\" value=\"(\\d+)\"").Groups[1].Value);

        (await SaveAsync(client, token, "Айрян Верея", "Millilitres", scanId)).StatusCode.Should().Be(HttpStatusCode.Redirect);

        await using var db = fixture.CreateContext(null);
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        var entry = await db.FoodLog.IgnoreQueryFilters().Include(e => e.FoodItem).SingleAsync(e => e.UserId == userId);
        entry.Unit.Should().Be(FoodUnit.Millilitres);
        entry.FoodItem!.Unit.Should().Be(FoodUnit.Millilitres);
        entry.Grams.Should().Be(250);
        entry.Kcal.Should().Be(95, "250 ml of a 38 kcal per 100 ml drink");
        var scan = await db.LabelScans.IgnoreQueryFilters().SingleAsync(s => s.Id == scanId);
        scan.Edited.Should().BeFalse("the unit was kept as read");
        scan.Saved.Should().Contain("\"unit\": \"Millilitres\"");

        var day = WebUtility.HtmlDecode(await client.GetStringAsync("/food?date=2026-10-05"));
        day.Should().Contain("· 250 ml");
        var page = WebUtility.HtmlDecode(await client.GetStringAsync($"/food/entries/{entry.Id}"));
        page.Should().Contain("250 ml").And.MatchRegex("id=\"grams\"[^>]*/><span class=\"unit\">ml</span>");
    }

    [Fact]
    public async Task Switching_a_label_to_grams_counts_as_an_edit()
    {
        var (app, _) = NewApp();
        await using var _app = app;
        var (client, email) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, AddPage);
        var scanId = int.Parse(System.Text.RegularExpressions.Regex.Match(await ReadLabelAsync(client, token), "name=\"labelScanId\" value=\"(\\d+)\"").Groups[1].Value);

        await SaveAsync(client, token, "Айрян", "Grams", scanId);

        await using var db = fixture.CreateContext(null);
        (await db.LabelScans.IgnoreQueryFilters().SingleAsync(s => s.Id == scanId)).Edited.Should().BeTrue();
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        (await db.FoodLog.IgnoreQueryFilters().SingleAsync(e => e.UserId == userId)).Unit.Should().Be(FoodUnit.Grams);
    }

    [Fact]
    public async Task An_unknown_unit_is_refused()
    {
        var (app, _) = NewApp();
        await using var _app = app;
        var (client, email) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, AddPage);

        var response = await SaveAsync(client, token, "Something", "Litres");

        response.Headers.Location?.OriginalString.Should().NotStartWith("/food?date", "the save didn't go through");
        await using var db = fixture.CreateContext(null);
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        (await db.FoodLog.IgnoreQueryFilters().AnyAsync(e => e.UserId == userId)).Should().BeFalse();
        (await db.FoodItems.IgnoreQueryFilters().AnyAsync(f => f.AddedByUserId == userId)).Should().BeFalse();
    }

    [Fact]
    public async Task The_unit_follows_the_entry_into_copies_saved_meals_recents_and_the_export()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var juice = new FoodItem { OwnerUserId = s.UserId, Name = "Orange juice", KcalPer100g = 45, ProteinPer100g = 0.7m, CarbsPer100g = 10, FatPer100g = 0.2m, Unit = FoodUnit.Millilitres, Source = FoodSource.Manual };
        s.Db.FoodItems.Add(juice);
        await s.Db.SaveChangesAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await s.FoodLog.AddAsync([new(today.AddDays(-1), Meal.Breakfast, "Orange juice", 300, juice.Per100(), juice.Id, EntrySource.Search)], default);

        (await s.FoodLog.CopyMealAsync(today.AddDays(-1), today, Meal.Breakfast, default)).Should().Be(1);
        var meal = await s.SavedMeals.SaveFromMealAsync(today, Meal.Breakfast, "Juice", default);
        await s.SavedMeals.LogAsync(meal.Id, today, Meal.Snack, [200m], default);

        var entries = await s.Db.FoodLog.OrderBy(e => e.Id).ToListAsync();
        entries.Should().HaveCount(3).And.OnlyContain(e => e.Unit == FoodUnit.Millilitres);
        (await s.Db.SavedMealItems.SingleAsync()).Unit.Should().Be(FoodUnit.Millilitres);
        (await s.FoodLog.RecentsAsync(today, Meal.Breakfast, default)).Single().Unit.Should().Be(FoodUnit.Millilitres);

        var files = ExportTests.Unzip(await new DataExport(s.Db).ZipAsync(default));
        files["food_log.csv"].Split("\r\n").Skip(1).Where(l => l.Length > 0).Should().HaveCount(3).And.OnlyContain(l => l.EndsWith(",ml"));
        files["saved_meal_items.csv"].Should().Contain(",ml\r\n");
        files["private_foods.csv"].Should().Contain(",Manual,ml\r\n");
    }

    [Fact]
    public async Task Rows_from_before_units_read_as_grams()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        // A row written without the column, as every row was before the migration added it with its default.
        await s.Db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "FoodLog" ("UserId", "Date", "Meal", "Description", "Grams", "Kcal", "ProteinG", "CarbsG", "FatG", "Source", "CreatedAt")
            VALUES ({s.UserId}, {new DateOnly(2026, 10, 1)}, 'Lunch', 'Old entry', 150, 200, 10, 20, 5, 'Manual', {DateTimeOffset.UtcNow})
            """);

        (await s.Db.FoodLog.SingleAsync()).Unit.Should().Be(FoodUnit.Grams);
    }

    [Fact]
    public void Stored_label_values_keep_their_unit_under_a_fixed_name()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        // A scan stored before per-100 ml labels were kept has no unit: it was read as per 100 g.
        const string before = """{"name":"Айрян","brand":null,"kcal":38,"protein":1.8,"carbs":2.6,"fat":2.2,"saturatedFat":1.4,"fiber":null,"servingGrams":250,"converted":0,"notes":[],"warnings":[]}""";
        JsonSerializer.Deserialize<Plate.Web.Features.Food.StoredLabelValues>(before, json)!.ToValues().Unit.Should().Be(FoodUnit.Grams);

        const string after = """{"name":"Айрян","brand":null,"kcal":38,"protein":1.8,"carbs":2.6,"fat":2.2,"saturatedFat":1.4,"fiber":null,"servingGrams":250,"converted":0,"notes":[],"warnings":[],"unit":"Millilitres"}""";
        JsonSerializer.Deserialize<Plate.Web.Features.Food.StoredLabelValues>(after, json)!.ToValues().Unit.Should().Be(FoodUnit.Millilitres);

        var written = JsonSerializer.Serialize(Plate.Web.Features.Food.StoredLabelValues.From(LabelValues.Normalize(Ayran).Values!), json);
        written.Should().Contain("\"unit\":\"Millilitres\"");
    }
}
