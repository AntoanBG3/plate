using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymTracker.Web.Tests;

/// <summary>Reading a label photo into the manual food form, and recording what was saved (SPEC 6.6).</summary>
public class LabelScanTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string AddPage = "/food/add?date=2026-10-05&meal=Lunch&tab=manual";

    private sealed record Account(HttpClient Client, string Email, string UserId, string Token);

    private static byte[] Jpeg(int size)
    {
        var bytes = new byte[size];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        return bytes;
    }

    private (AppFactory App, FakeLabelReader Reader) NewApp(Dictionary<string, string?>? settings = null)
    {
        var reader = new FakeLabelReader();
        var app = new AppFactory(fixture, settings: settings, services: s => s
            .AddScoped<ILabelReader>(_ => reader)
            .AddSingleton<IFoodDatabase, FakeFoodDatabase>());
        return (app, reader);
    }

    private async Task<Account> SignInAsync(AppFactory app)
    {
        var (client, email) = await app.SignedInClientAsync();
        await using var db = fixture.CreateContext(null);
        var id = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        return new Account(client, email, id, await AppFactory.TokenAsync(client, AddPage));
    }

    private static Task<HttpResponseMessage> ReadAsync(Account a, byte[] photo, string? name = null, string? barcode = null, bool barcodeField = true, bool? share = null,
        string? grams = null, string? servingGrams = null) =>
        ReadManyAsync(a, [photo], name, barcode, barcodeField, share, grams, servingGrams);

    private static Task<HttpResponseMessage> ReadManyAsync(Account a, IReadOnlyList<byte[]> photos, string? name = null, string? barcode = null, bool barcodeField = true, bool? share = null,
        string? grams = null, string? servingGrams = null)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("2026-10-05"), "date" },
            { new StringContent("Lunch"), "meal" },
            { new StringContent(barcodeField ? "true" : "false"), "barcodeField" },
        };
        if (name is not null)
        {
            form.Add(new StringContent(name), "name");
        }

        if (barcode is not null)
        {
            form.Add(new StringContent(barcode), "barcode");
        }

        if (share is { } s)
        {
            form.Add(new StringContent(s ? "true" : "false"), "share");
        }

        if (grams is not null)
        {
            form.Add(new StringContent(grams), "grams");
        }

        if (servingGrams is not null)
        {
            form.Add(new StringContent(servingGrams), "servingGrams");
        }

        for (var i = 0; i < photos.Count; i++)
        {
            var file = new ByteArrayContent(photos[i]);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(file, "photo", $"label{i}.jpg");
            form.Add(new StringContent((1200 - 200 * i).ToString()), "width");
            form.Add(new StringContent("900"), "height");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "/food/label") { Content = form };
        request.Headers.Add("RequestVerificationToken", a.Token);
        request.Headers.Add("HX-Request", "true");
        return a.Client.SendAsync(request);
    }

    private static async Task<string> HtmlAsync(HttpResponseMessage response) =>
        WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

    private static int ScanIdIn(string html) =>
        int.Parse(System.Text.RegularExpressions.Regex.Match(html, "name=\"labelScanId\" value=\"(\\d+)\"").Groups[1].Value);

    private static Task<HttpResponseMessage> SaveAsync(Account a, int? scanId, string protein)
    {
        var fields = new Dictionary<string, string>
        {
            ["date"] = "2026-10-05",
            ["meal"] = "Lunch",
            ["name"] = "Yogurt " + Guid.NewGuid().ToString("N")[..6],
            ["kcal"] = "62",
            ["protein"] = protein,
            ["carbs"] = "4.1",
            ["fat"] = "3.6",
            ["saturatedFat"] = "2.3",
            ["servingGrams"] = "400",
            ["grams"] = "150",
        };
        if (scanId is { } id)
        {
            fields["labelScanId"] = id.ToString();
        }

        fields["requestId"] = Guid.NewGuid().ToString();
        return a.Client.PostAsync("/food/items", new FormUrlEncodedContent(fields.Append(new("__RequestVerificationToken", a.Token))));
    }

    [Fact]
    public async Task A_photo_fills_the_form_with_the_labels_values()
    {
        var (app, _) = NewApp();
        await using var _app = app;
        var a = await SignInAsync(app);

        var response = await ReadAsync(a, Jpeg(5000));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await HtmlAsync(response);
        html.Should().Contain("Read from the photo. Check each value against the label.");
        html.Should().Contain("id=\"m-kcal\"").And.MatchRegex("id=\"m-kcal\"[^>]*value=\"62\"");
        html.Should().MatchRegex("id=\"m-protein\"[^>]*value=\"3.2\"");
        html.Should().MatchRegex("id=\"m-serving\"[^>]*value=\"400\"");
        html.Should().MatchRegex("id=\"m-grams\"[^>]*value=\"400\"");
        html.Should().MatchRegex("id=\"m-name\"[^>]*value=\"Кисело мляко 3.6%\"");
        ScanIdIn(html).Should().BePositive();
    }

    [Fact]
    public async Task Each_read_stores_a_scan_and_its_photo()
    {
        var (app, _) = NewApp();
        await using var _app = app;
        var a = await SignInAsync(app);

        await ReadAsync(a, Jpeg(5000));

        await using var db = fixture.CreateContext(a.UserId);
        var scan = await db.LabelScans.Include(s => s.Photos).SingleAsync();
        (scan.Model, scan.Error, scan.Values is null).Should().Be(("test/label", (string?)null, false));
        var photo = scan.Photos.Single();
        (photo.Position, photo.ContentType, photo.Bytes.Length, photo.Width).Should().Be((0, "image/jpeg", 5000, 1200));
    }

    [Fact]
    public async Task A_failed_read_is_stored_and_answers_with_the_message_only()
    {
        // The page keeps the form the user filled in; only the message comes back (SPEC 6.6: "leaves the form untouched").
        var (app, reader) = NewApp();
        await using var _app = app;
        reader.Next = LabelReadResult.Failed("Couldn't read the label. Enter the values by hand.", "test/label");
        var a = await SignInAsync(app);

        var response = await ReadAsync(a, Jpeg(5000), name: "My ayran");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Be("Couldn't read the label. Enter the values by hand.");
        await using var db = fixture.CreateContext(a.UserId);
        (await db.LabelScans.SingleAsync()).Error.Should().Be("Couldn't read the label. Enter the values by hand.");
    }

    [Fact]
    public async Task An_unticked_share_choice_stays_unticked_after_a_read()
    {
        var (app, _) = NewApp();
        await using var _app = app;
        var a = await SignInAsync(app);

        var html = await HtmlAsync(await ReadAsync(a, Jpeg(100), barcode: "5901234123457", barcodeField: false, share: false));

        html.Should().MatchRegex("<input type=\"checkbox\" class=\"switch\" name=\"share\" value=\"true\" />");
        (await HtmlAsync(await ReadAsync(a, Jpeg(100), barcode: "5901234123457", barcodeField: false))).Should()
            .MatchRegex("<input type=\"checkbox\" class=\"switch\" name=\"share\" value=\"true\" checked=\"checked\" />", "ticked unless the user unticked it");
    }

    [Fact]
    public async Task A_typed_name_wins_over_the_name_on_the_label()
    {
        var (app, _) = NewApp();
        await using var _app = app;
        var a = await SignInAsync(app);

        (await HtmlAsync(await ReadAsync(a, Jpeg(5000), name: "Моят айрян"))).Should().MatchRegex("id=\"m-name\"[^>]*value=\"Моят айрян\"");
    }

    [Fact]
    public async Task A_photo_without_a_nutrition_table_says_so()
    {
        var (app, reader) = NewApp();
        await using var _app = app;
        reader.Next = new LabelReadResult(FakeLabelReader.Yogurt with { IsLabel = false }, "{}", null, "test/label");
        var a = await SignInAsync(app);

        var response = await ReadAsync(a, Jpeg(5000));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Be("Couldn't find a nutrition table in that photo.");
    }

    [Fact]
    public async Task Something_that_is_not_an_image_is_refused_before_the_model()
    {
        var (app, reader) = NewApp();
        await using var _app = app;
        var a = await SignInAsync(app);

        var response = await ReadAsync(a, "hello"u8.ToArray());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Be("Attach a photo (JPEG, PNG or WebP).");
        reader.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_photo_over_1_MB_is_read_and_one_over_2_MB_is_refused()
    {
        var (app, reader) = NewApp();
        await using var _app = app;
        var a = await SignInAsync(app);

        (await ReadAsync(a, Jpeg(1_500_000))).StatusCode.Should().Be(HttpStatusCode.OK);

        var big = await ReadAsync(a, Jpeg(2_500_000));
        big.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await big.Content.ReadAsStringAsync()).Should().Be("That photo is too large.");
        reader.Calls.Should().Be(1);

        // Over the endpoint's 7 MB limit Kestrel answers 413; the test server reads the body, so the app's own check answers.
        (await ReadAsync(a, Jpeg(7_500_000))).StatusCode.Should().BeOneOf(HttpStatusCode.RequestEntityTooLarge, HttpStatusCode.BadRequest);
        reader.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Label_reads_are_rate_limited_per_user()
    {
        var (app, _) = NewApp(new Dictionary<string, string?> { ["RateLimits:AiLabelPerWindow"] = "2" });
        await using var _app = app;
        var a = await SignInAsync(app);

        (await ReadAsync(a, Jpeg(100))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(a, Jpeg(100))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(a, Jpeg(100))).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Without_a_key_the_endpoint_is_not_there()
    {
        await using var app = new AppFactory(fixture);
        var a = await SignInAsync(app);

        (await ReadAsync(a, Jpeg(100))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Saving_records_whether_the_values_were_edited()
    {
        var (app, _) = NewApp();
        await using var _app = app;
        var a = await SignInAsync(app);

        var edited = ScanIdIn(await HtmlAsync(await ReadAsync(a, Jpeg(100))));
        (await SaveAsync(a, edited, protein: "4.0")).StatusCode.Should().Be(HttpStatusCode.Redirect);
        var unchanged = ScanIdIn(await HtmlAsync(await ReadAsync(a, Jpeg(100))));
        (await SaveAsync(a, unchanged, protein: "3.2")).StatusCode.Should().Be(HttpStatusCode.Redirect);

        await using var db = fixture.CreateContext(a.UserId);
        var scans = await db.LabelScans.ToDictionaryAsync(s => s.Id);
        (scans[edited].Edited, scans[edited].FoodItemId is null, scans[edited].Saved is null).Should().Be((true, false, false));
        scans[unchanged].Edited.Should().BeFalse();
        JsonDocument.Parse(scans[unchanged].Saved!).RootElement.GetProperty("proteinPer100g").GetDecimal().Should().Be(3.2m);
    }

    [Fact]
    public async Task Another_users_scan_id_is_ignored_and_the_food_still_saves()
    {
        var (app, _) = NewApp();
        await using var _app = app;
        var a = await SignInAsync(app);
        var b = await SignInAsync(app);
        var theirs = ScanIdIn(await HtmlAsync(await ReadAsync(a, Jpeg(100))));

        (await SaveAsync(b, theirs, protein: "3.2")).StatusCode.Should().Be(HttpStatusCode.Redirect);

        await using var db = fixture.CreateContext(a.UserId);
        var scan = await db.LabelScans.SingleAsync(s => s.Id == theirs);
        (scan.FoodItemId, scan.Saved).Should().Be(((int?)null, (string?)null));
    }

    [Fact]
    public async Task A_Bulgarian_account_gets_values_with_a_dot()
    {
        var (app, reader) = NewApp();
        await using var _app = app;
        var a = await SignInAsync(app);
        await using (var db = fixture.CreateContext(a.UserId))
        {
            await db.Profiles.ExecuteUpdateAsync(p => p.SetProperty(x => x.Language, Language.Bulgarian));
        }

        var html = await HtmlAsync(await ReadAsync(a, Jpeg(100)));

        html.Should().MatchRegex("id=\"m-protein\"[^>]*value=\"3.2\"").And.NotContain("value=\"3,2\"");
        html.Should().Contain("Прочетено от снимката. Провери всяка стойност спрямо етикета.");
    }

    private const string PrivacyLine = "The photo is sent to an AI model through OpenRouter and kept for 30 days so misreads can be checked.";

    [Fact]
    public async Task The_manual_tab_offers_a_label_photo_and_an_optional_barcode()
    {
        var (app, _) = NewApp();
        await using var _app = app;
        var a = await SignInAsync(app);

        var html = WebUtility.HtmlDecode(await a.Client.GetStringAsync(AddPage));

        html.Should().Contain("Read the label from a photo").And.Contain("data-label-photo").And.Contain(PrivacyLine);
        // The label photo offers camera and library (no capture); the scanner sheet's own photo button may use the camera.
        html.Should().MatchRegex("<input type=\"file\" accept=\"image/\\*\" class=\"sr-only\" data-label-photo />");
        html.Should().Contain("data-barcode-input").And.Contain("data-module=\"label\"");
    }

    [Fact]
    public async Task A_barcode_miss_offers_a_label_photo_and_shows_the_share_choice()
    {
        var (app, _) = NewApp();
        await using var _app = app;
        var a = await SignInAsync(app);

        var html = WebUtility.HtmlDecode(await a.Client.GetStringAsync("/food/barcode?date=2026-10-05&meal=Lunch&code=5901234123457"));

        html.Should().Contain("data-label-photo").And.Contain("Share with other users");
        html.Should().MatchRegex("<div class=\"list\" data-share[^>]*>").And.NotMatchRegex("<div class=\"list\" data-share[^>]*hidden");
    }

    [Fact]
    public async Task The_scan_tab_offers_to_photograph_a_label()
    {
        var (app, _) = NewApp();
        await using var _app = app;
        var a = await SignInAsync(app);

        var html = WebUtility.HtmlDecode(await a.Client.GetStringAsync("/food/add?date=2026-10-05&meal=Lunch&tab=barcode"));

        html.Should().Contain("Photograph a label").And.Contain("data-label-slot").And.Contain(PrivacyLine);
    }

    [Fact]
    public async Task Without_a_label_model_no_photo_button_shows()
    {
        var (app, reader) = NewApp();
        await using var _app = app;
        reader.Available = false;
        var a = await SignInAsync(app);

        (await a.Client.GetStringAsync(AddPage)).Should().NotContain("data-label-photo");
        (await a.Client.GetStringAsync("/food/add?date=2026-10-05&meal=Lunch&tab=barcode")).Should().NotContain("Photograph a label");
    }

    [Fact]
    public async Task Converted_and_missing_values_are_marked_and_the_notes_listed()
    {
        var (app, reader) = NewApp();
        await using var _app = app;
        reader.Next = new LabelReadResult(new LabelReading(true, null, null, LabelBasis.PerServing, 30, 120, null, 3, 18, 4, null, null), "{}", null, "test/label");
        var a = await SignInAsync(app);

        var html = await HtmlAsync(await ReadAsync(a, Jpeg(100)));

        html.Should().MatchRegex("id=\"m-kcal\" class=\"num is-converted\"[^>]*value=\"400\"");
        html.Should().MatchRegex("id=\"m-protein\" class=\"num is-converted\"[^>]*value=\"10\"");
        html.Should().MatchRegex("id=\"m-fiber\" class=\"num is-missing\"");
        html.Should().Contain("Read from the photo. Check each value against the label.");
    }

    [Fact]
    public async Task A_read_whose_request_was_cancelled_is_still_stored()
    {
        // A second photo or leaving the page aborts the request, but the model may already have been paid for.
        var userId = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext(userId);
        var service = new GymTracker.Web.Features.Food.LabelScanService(db, new FakeLabelReader(), new GymTracker.Web.Data.FixedCurrentUser(userId), TimeProvider.System);
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        await service.ReadAsync([new GymTracker.Web.Features.Food.LabelPhotoUpload(Jpeg(100), 10, 10)], aborted.Token);

        (await db.LabelScans.CountAsync(s => s.UserId == userId)).Should().Be(1);
    }

    [Fact]
    public async Task A_failure_without_a_model_is_stored_as_unknown_not_blank()
    {
        var userId = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext(userId);
        var reader = new FakeLabelReader { Next = LabelReadResult.Failed("Couldn't read the label. Enter the values by hand.") };
        var service = new GymTracker.Web.Features.Food.LabelScanService(db, reader, new GymTracker.Web.Data.FixedCurrentUser(userId), TimeProvider.System);

        await service.ReadAsync([new GymTracker.Web.Features.Food.LabelPhotoUpload(Jpeg(100), 10, 10)], default);

        (await db.LabelScans.SingleAsync(s => s.UserId == userId)).Model.Should().Be("unknown");
    }

    [Fact]
    public async Task Amounts_typed_before_the_photo_are_kept()
    {
        // "You ate" typed first stays; the label's serving only fills it when it's empty (SPEC 6.6 rule 6).
        var (app, reader) = NewApp();
        await using var _app = app;
        var a = await SignInAsync(app);

        var withServing = await HtmlAsync(await ReadAsync(a, Jpeg(100), grams: "80"));
        withServing.Should().MatchRegex("id=\"m-grams\"[^>]*value=\"80\"").And.MatchRegex("id=\"m-serving\"[^>]*value=\"400\"");

        reader.Next = new LabelReadResult(FakeLabelReader.Yogurt with { ServingGrams = null }, "{}", null, "test/label");
        var noServing = await HtmlAsync(await ReadAsync(a, Jpeg(100), grams: "80", servingGrams: "30"));
        noServing.Should().MatchRegex("id=\"m-grams\"[^>]*value=\"80\"").And.MatchRegex("id=\"m-serving\"[^>]*value=\"30\"");
    }

    [Fact]
    public async Task The_manual_tab_has_a_scan_button_beside_the_barcode_field_and_the_scanner_sheet()
    {
        var (app, _) = NewApp();
        await using var _app = app;
        var a = await SignInAsync(app);

        var html = await a.Client.GetStringAsync(AddPage);

        html.Should().MatchRegex("data-barcode-input[^>]*/>\\s*<button type=\"button\" class=\"icon-btn\" data-scan-into");
        html.Should().Contain("id=\"scanner\"").And.Contain("/js/scanner.js").And.Contain("barcode-detector-3.2.2.ponyfill.js");
    }

    [Fact]
    public async Task The_form_after_a_barcode_miss_has_no_scan_button()
    {
        var (app, _) = NewApp();
        await using var _app = app;
        var a = await SignInAsync(app);

        var html = await a.Client.GetStringAsync("/food/barcode?date=2026-10-05&meal=Lunch&code=4006381333931");

        html.Should().NotContain("data-scan-into");
    }

    [Fact]
    public async Task Three_photos_are_read_together_and_stored_in_order()
    {
        var (app, reader) = NewApp();
        await using var _app = app;
        var a = await SignInAsync(app);

        var response = await ReadManyAsync(a, [Jpeg(5000), Jpeg(4000), Jpeg(3000)]);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await HtmlAsync(response)).Should().Contain("Read from the photo. Check each value against the label.");
        reader.Calls.Should().Be(1);
        reader.LastImages!.Select(i => i.Bytes.Length).Should().Equal(5000, 4000, 3000);
        await using var db = fixture.CreateContext(a.UserId);
        var scan = await db.LabelScans.Include(s => s.Photos).SingleAsync();
        scan.Photos.OrderBy(p => p.Position).Select(p => (p.Position, p.Bytes.Length, p.Width)).Should().Equal((0, 5000, 1200), (1, 4000, 1000), (2, 3000, 800));
    }

    [Fact]
    public async Task A_fourth_photo_is_refused_before_the_model_is_called()
    {
        var (app, reader) = NewApp();
        await using var _app = app;
        var a = await SignInAsync(app);

        var response = await ReadManyAsync(a, [Jpeg(100), Jpeg(100), Jpeg(100), Jpeg(100)]);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Attach one to three photos.");
        reader.Calls.Should().Be(0);
    }

    [Fact]
    public async Task One_bad_photo_among_three_refuses_the_read()
    {
        var (app, reader) = NewApp();
        await using var _app = app;
        var a = await SignInAsync(app);

        var response = await ReadManyAsync(a, [Jpeg(100), "not an image"u8.ToArray(), Jpeg(100)]);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        reader.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_three_photo_read_counts_once_toward_the_limit()
    {
        var (app, _) = NewApp(new Dictionary<string, string?> { ["RateLimits:AiLabelPerWindow"] = "2" });
        await using var _app = app;
        var a = await SignInAsync(app);

        (await ReadManyAsync(a, [Jpeg(100), Jpeg(100), Jpeg(100)])).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(a, Jpeg(100))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(a, Jpeg(100))).StatusCode.Should().Be((HttpStatusCode)429);
    }

    [Fact]
    public async Task The_extra_photo_slots_are_there_but_closed_until_asked_for()
    {
        var (app, _) = NewApp();
        await using var _app = app;
        var a = await SignInAsync(app);

        foreach (var path in new[] { AddPage, "/food/add?date=2026-10-05&meal=Lunch&tab=barcode" })
        {
            var html = WebUtility.HtmlDecode(await a.Client.GetStringAsync(path));
            html.Should().Contain("data-label-more").And.Contain("Add more photos").And.Contain("Nutrition table");
            System.Text.RegularExpressions.Regex.Matches(html, "data-label-slot-photo").Count.Should().Be(3, path);
            html.Should().MatchRegex("data-label-read[^>]*disabled");
        }
    }
}
