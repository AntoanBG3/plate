using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Cli;
using GymTracker.Web.Features.Feedback;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymTracker.Web.Tests;

/// <summary>Reports, parse ratings, screenshots, write-back, errors and the CLI (SPEC 11).</summary>
[Collection(PostgresCollection.Name)]
public partial class FeedbackTests(PostgresFixture fixture)
{
    // A 1x1 PNG.
    internal static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    internal static Task<HttpResponseMessage> QueuePost(HttpClient client, string path, string token, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("HX-Request", "true");
        request.Headers.Add("X-Gym-Queue", "1");
        return client.SendAsync(request);
    }

    internal static async Task<int> IdOf(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<int>();
    }

    private static async Task<int> ParseAsync(HttpClient client, string token, string text = "150g banana, 200g chicken breast raw")
    {
        var response = await AppFactory.PostAsync(client, "/food/parse", token, new() { ["date"] = "2026-10-04", ["meal"] = "Lunch", ["text"] = text });
        var html = await response.Content.ReadAsStringAsync();
        return int.Parse(RunIdField().Match(html).Groups[1].Value);
    }

    internal static Dictionary<string, string> Report(string message, Guid? clientId = null, string area = "Speed") => new()
    {
        ["clientId"] = (clientId ?? Guid.NewGuid()).ToString(),
        ["kind"] = "Problem",
        ["area"] = area,
        ["source"] = "Settings",
        ["message"] = message,
        ["page"] = "/today",
        ["viewport"] = "390x844",
        ["standalone"] = "true",
        ["recentErrors"] = "[{\"message\":\"boom\",\"page\":\"/train\"}]",
    };

    private async Task<Core.Domain.Feedback> LoadAsync(int id)
    {
        await using var db = fixture.CreateContext(null);
        return await db.Feedback.IgnoreQueryFilters().Include(f => f.Attachment).SingleAsync(f => f.Id == id);
    }

    [Fact]
    public async Task A_thumbs_down_records_the_run_and_item_and_its_reasons_follow()
    {
        using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/food/add");
        var runId = await ParseAsync(client, token);
        var clientId = Guid.NewGuid().ToString();

        var id = await IdOf(await QueuePost(client, $"/feedback/parse/{runId}", token, new Dictionary<string, string> { ["clientId"] = clientId, ["rating"] = "Down", ["item"] = "1" }));
        (await IdOf(await QueuePost(client, $"/feedback/parse/{runId}", token, new Dictionary<string, string> { ["clientId"] = clientId, ["rating"] = "Down", ["item"] = "1" })))
            .Should().Be(id, "a retried rating is the same rating");
        var details = await QueuePost(client, "/feedback/parse/details", token,
            [new("clientId", clientId), new("reasons", "WrongFood"), new("reasons", "WrongAmount"), new("message", "It was thigh, not breast")]);
        details.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var feedback = await LoadAsync(id);
        (feedback.Source, feedback.Rating, feedback.ParseRunId, feedback.ParseItemIndex).Should().Be((FeedbackSource.ParseRating, FeedbackRating.Down, runId, 1));
        feedback.Reasons.Should().Be(ParseReason.WrongFood | ParseReason.WrongAmount);
        feedback.Message.Should().Be("It was thigh, not breast");
        JsonNode.Parse(feedback.Context)!["viewport"].Should().BeNull("this request sent none");
    }

    [Fact]
    public async Task Changing_a_rating_replaces_it_and_bad_items_or_foreign_runs_are_refused()
    {
        using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/food/add");
        var runId = await ParseAsync(client, token);

        var up = await IdOf(await QueuePost(client, $"/feedback/parse/{runId}", token, new Dictionary<string, string> { ["clientId"] = Guid.NewGuid().ToString(), ["rating"] = "Up" }));
        var down = await IdOf(await QueuePost(client, $"/feedback/parse/{runId}", token, new Dictionary<string, string> { ["clientId"] = Guid.NewGuid().ToString(), ["rating"] = "Down" }));
        down.Should().Be(up);
        (await LoadAsync(up)).Rating.Should().Be(FeedbackRating.Down);

        (await QueuePost(client, $"/feedback/parse/{runId}", token, new Dictionary<string, string> { ["clientId"] = Guid.NewGuid().ToString(), ["rating"] = "Down", ["item"] = "9" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var (other, _) = await app.SignedInClientAsync();
        var otherToken = await AppFactory.TokenAsync(other, "/food/add");
        (await QueuePost(other, $"/feedback/parse/{runId}", otherToken, new Dictionary<string, string> { ["clientId"] = Guid.NewGuid().ToString(), ["rating"] = "Down" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_report_keeps_its_context_and_a_retry_is_the_same_report()
    {
        using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/feedback/new");
        var clientId = Guid.NewGuid();

        var id = await IdOf(await QueuePost(client, "/feedback", token, Report("The Today page is slow", clientId)));
        (await IdOf(await QueuePost(client, "/feedback", token, Report("The Today page is slow", clientId)))).Should().Be(id);

        var feedback = await LoadAsync(id);
        (feedback.Kind, feedback.Area, feedback.Status, feedback.Message).Should().Be((FeedbackKind.Problem, FeedbackArea.Speed, FeedbackStatus.New, "The Today page is slow"));
        var context = JsonNode.Parse(feedback.Context)!;
        context["page"]!.GetValue<string>().Should().Be("/today");
        context["viewport"]!.GetValue<string>().Should().Be("390x844");
        context["standalone"]!.GetValue<bool>().Should().BeTrue();
        context["version"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        context["recentErrors"]![0]!["message"]!.GetValue<string>().Should().Be("boom");
        context["localTime"].Should().NotBeNull();
    }

    [Fact]
    public async Task Reports_need_words_and_a_known_area_and_a_foreign_client_id_is_refused()
    {
        using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/feedback/new");

        (await QueuePost(client, "/feedback", token, Report("   "))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await QueuePost(client, "/feedback", token, Report("x", area: "Nonsense"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var clientId = Guid.NewGuid();
        await IdOf(await QueuePost(client, "/feedback", token, Report("mine", clientId)));
        var (other, _) = await app.SignedInClientAsync();
        var otherToken = await AppFactory.TokenAsync(other, "/feedback/new");
        (await QueuePost(other, "/feedback", otherToken, Report("theirs", clientId))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Without_script_a_report_redirects_to_the_list_with_a_thank_you()
    {
        using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();
        var html = await client.GetStringAsync("/feedback/new");
        var token = await AppFactory.TokenAsync(client, "/feedback/new");
        var clientId = ClientIdField().Match(html).Groups[1].Value;
        clientId.Should().NotBeEmpty();

        var request = new HttpRequestMessage(HttpMethod.Post, "/feedback")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>(Report("Plain form post", Guid.Parse(clientId))) { ["__RequestVerificationToken"] = token }),
        };
        var response = await client.SendAsync(request);

        AppFactory.IsRedirectTo(response, "/feedback").Should().BeTrue();
        var list = await client.GetStringAsync("/feedback");
        list.Should().Contain("Plain form post").And.Contain("Thanks. The answer will show here.");
    }

    [Fact]
    public async Task Screenshots_are_checked_by_their_bytes_and_size_and_only_the_owner_sees_them()
    {
        using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/feedback/new");

        async Task<HttpResponseMessage> Upload(byte[] bytes, string type)
        {
            var form = new MultipartFormDataContent();
            foreach (var (k, v) in Report("See the picture"))
            {
                form.Add(new StringContent(v), k);
            }

            form.Add(new StringContent("1"), "screenshotWidth");
            form.Add(new StringContent("1"), "screenshotHeight");
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(type);
            form.Add(file, "screenshot", "shot");
            var request = new HttpRequestMessage(HttpMethod.Post, "/feedback") { Content = form };
            request.Headers.Add("RequestVerificationToken", token);
            request.Headers.Add("HX-Request", "true");
            request.Headers.Add("X-Gym-Queue", "1");
            return await client.SendAsync(request);
        }

        var id = await IdOf(await Upload(Png, "image/png"));
        (await Upload("<svg onload=alert(1)>"u8.ToArray(), "image/png")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var big = new byte[FeedbackAttachment.MaxBytes + 1];
        Png.CopyTo(big, 0);
        (await Upload(big, "image/png")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var stored = await LoadAsync(id);
        stored.Attachment!.ContentType.Should().Be("image/png");
        var image = await client.GetAsync($"/feedback/{id}/screenshot");
        image.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await image.Content.ReadAsByteArrayAsync()).Should().Equal(Png);

        var (other, _) = await app.SignedInClientAsync();
        (await other.GetAsync($"/feedback/{id}/screenshot")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_status_and_reply_from_the_CLI_show_on_the_reporters_list_once_as_unread()
    {
        using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/feedback/new");
        var id = await IdOf(await QueuePost(client, "/feedback", token, Report("Rest timer beeps twice")));

        var output = new StringWriter();
        (await FeedbackCommand.RunAsync(app.Services, ["feedback", "status", id.ToString(), "fixed", "--reply", "Fixed, thanks!", "--version", "abc1234"], output)).Should().Be(0);
        output.ToString().Should().Contain($"#{id} is now Fixed");

        (await client.GetStringAsync("/settings")).Should().Contain("count-badge");
        var list = await client.GetStringAsync("/feedback");
        list.Should().Contain("Fixed, thanks!").And.Contain("is-unread");
        (await client.GetStringAsync("/settings")).Should().NotContain("count-badge", "opening the list marks replies read");
        (await client.GetStringAsync("/feedback")).Should().NotContain("is-unread");

        var (other, _) = await app.SignedInClientAsync();
        (await other.GetStringAsync("/feedback")).Should().NotContain("Rest timer beeps twice");
    }

    [Fact]
    public async Task The_CLI_lists_shows_and_exports_with_the_parse_run()
    {
        using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/food/add");
        var runId = await ParseAsync(client, token, "200g chicken breast raw");
        var clientId = Guid.NewGuid().ToString();
        var id = await IdOf(await QueuePost(client, $"/feedback/parse/{runId}", token, new Dictionary<string, string> { ["clientId"] = clientId, ["rating"] = "Down", ["item"] = "0" }));
        await QueuePost(client, "/feedback/parse/details", token, [new("clientId", clientId), new("reasons", "WrongFood"), new("message", "cli check")]);

        var list = new StringWriter();
        await FeedbackCommand.RunAsync(app.Services, ["feedback", "list", "--since", "1h"], list);
        list.ToString().Should().Contain($"#{id}").And.Contain("cli check").And.Contain($"run {runId}");

        var show = new StringWriter();
        await FeedbackCommand.RunAsync(app.Services, ["feedback", "show", id.ToString()], show);
        show.ToString().Should().Contain("Parse run").And.Contain("\"200g chicken breast raw\"").And.Contain("candidate #").And.Contain("Context:").And.Contain("WrongFood");

        var json = new StringWriter();
        await FeedbackCommand.RunAsync(app.Services, ["feedback", "show", id.ToString(), "--json"], json);
        JsonNode.Parse(json.ToString())!["row"]!["id"]!.GetValue<int>().Should().Be(id);

        (await FeedbackCommand.RunAsync(app.Services, ["feedback", "show", "999999"], new StringWriter())).Should().Be(1);
        (await FeedbackCommand.RunAsync(app.Services, ["feedback", "frobnicate"], new StringWriter())).Should().Be(2);
    }

    [Fact]
    public async Task The_CLI_streams_a_screenshot_as_raw_bytes()
    {
        using var app = new AppFactory(fixture);
        var userId = await fixture.CreateUserAsync();
        int id;
        await using (var db = fixture.CreateContext(userId))
        {
            var f = new Core.Domain.Feedback
            {
                ClientId = Guid.NewGuid(), UserId = userId, CreatedAt = DateTimeOffset.UtcNow, Message = "with a picture",
                Attachment = new FeedbackAttachment { ContentType = "image/png", Width = 1, Height = 1, Bytes = Png },
            };
            db.Feedback.Add(f);
            await db.SaveChangesAsync();
            id = f.Id;
        }

        using var bytes = new MemoryStream();
        (await FeedbackCommand.RunAsync(app.Services, ["feedback", "attachment", id.ToString()], new StringWriter(), bytes)).Should().Be(0);
        bytes.ToArray().Should().Equal(Png);
    }

    [Fact]
    public async Task Contextual_reports_snapshot_what_they_are_about_and_foreign_ids_are_not_found()
    {
        using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/food/add");
        var runId = await ParseAsync(client, token, "150g banana");
        var confirm = new Dictionary<string, string>
        {
            ["date"] = "2026-10-04", ["meal"] = "Lunch", ["aiModel"] = "fake", ["parseRunId"] = runId.ToString(),
            ["items[0].Include"] = "true", ["items[0].Description"] = "banana", ["items[0].Grams"] = "150",
            ["items[0].Kcal"] = "89", ["items[0].Protein"] = "1.1", ["items[0].Carbs"] = "22.8", ["items[0].Fat"] = "0.3",
        };
        await AppFactory.PostAsync(client, "/food/confirm", token, confirm);
        await using var db = fixture.CreateContext(null);
        var entry = await db.FoodLog.IgnoreQueryFilters().SingleAsync(e => e.ParseRunId == runId);

        (await client.GetStringAsync($"/food/entries/{entry.Id}")).Should().Contain($"/feedback/new?about=FoodEntry&id={entry.Id}");
        var form = await client.GetStringAsync($"/feedback/new?about=FoodEntry&id={entry.Id}");
        form.Should().Contain("About: banana").And.Contain("name=\"reasons\"");

        var fields = Report("Not a whole banana");
        fields["source"] = "FoodEntry";
        fields["aboutId"] = entry.Id.ToString();
        var id = await IdOf(await QueuePost(client, "/feedback", token, [.. fields, new("reasons", "WrongAmount")]));
        var feedback = await LoadAsync(id);
        (feedback.ParseRunId, feedback.Reasons, feedback.Rating).Should().Be((runId, ParseReason.WrongAmount, FeedbackRating.Down));
        JsonNode.Parse(feedback.Context)!["about"]!["description"]!.GetValue<string>().Should().Be("banana");

        var (other, _) = await app.SignedInClientAsync();
        (await other.GetAsync($"/feedback/new?about=FoodEntry&id={entry.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.GetAsync($"/feedback/new?about=Maintenance&id={int.MaxValue}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Errors_are_grouped_by_fingerprint_and_counted()
    {
        using var app = new AppFactory(fixture);
        var recorder = app.Services.GetRequiredService<ErrorRecorder>();
        var type = "TestError" + Guid.NewGuid().ToString("N")[..8];

        await recorder.RecordAsync(new ErrorReport(ErrorOrigin.Server, type, "Entry 12 broke", "at GymTracker.X.Y()", "/food/entries/12", null, "t1", "v1"), default);
        await recorder.RecordAsync(new ErrorReport(ErrorOrigin.Server, type, "Entry 99 broke", "at GymTracker.X.Y()", "/food/entries/99", null, "t2", "v2"), default);

        await using var db = fixture.CreateContext(null);
        var error = await db.ErrorEvents.SingleAsync(e => e.Type == type);
        (error.Count, error.LastTraceId, error.FirstVersion, error.LastVersion).Should().Be((2, "t2", "v1", "v2"));
    }

    [Fact]
    public async Task An_unhandled_exception_is_recorded_and_the_error_page_offers_a_report()
    {
        using var app = new AppFactory(fixture, services: s => s.AddScoped<IFoodParser, ThrowingParser>());
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/food/add");

        var request = new HttpRequestMessage(HttpMethod.Post, "/food/parse")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["date"] = "2026-10-04", ["meal"] = "Lunch", ["text"] = "toast", ["__RequestVerificationToken"] = token }),
        };
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync()).Should().Contain("/feedback/new?about=ErrorPage&trace=");
        await using var db = fixture.CreateContext(null);
        var error = await db.ErrorEvents.SingleAsync(e => e.Message == ThrowingParser.Message);
        (error.Origin, error.Type, error.Path).Should().Be((ErrorOrigin.Server, "InvalidOperationException", "/food/parse"));
        error.Stack.Should().Contain("ThrowingParser");
    }

    [Fact]
    public async Task Browser_errors_are_recorded_small_and_typed()
    {
        using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/today");
        var message = "Uncaught TypeError: x" + Guid.NewGuid().ToString("N")[..6] + " is undefined";

        (await AppFactory.PostAsync(client, "/feedback/client-error", token, new() { ["message"] = message, ["stack"] = "render@https://gym.peychev.uk/js/nav.js:10:5", ["page"] = "/train" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await AppFactory.PostAsync(client, "/feedback/client-error", token, new() { ["message"] = "x", ["stack"] = new string('a', 9000) }))
            .StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);

        await using var db = fixture.CreateContext(null);
        var expected = message["Uncaught TypeError: ".Length..];
        var error = await db.ErrorEvents.SingleAsync(e => e.Message == expected);
        (error.Origin, error.Type, error.Path).Should().Be((ErrorOrigin.Client, "TypeError", "/train"));
    }

    [Fact]
    public async Task Telegram_gets_the_report_head_and_never_its_snapshot()
    {
        var telegram = new RecordingHandler();
        using var app = new AppFactory(fixture,
            settings: new Dictionary<string, string?> { ["Telegram:BotToken"] = "123:secret", ["Telegram:ChatId"] = "42", ["Telegram:BaseUrl"] = "http://telegram.test/" },
            services: s => s.AddHttpClient(FeedbackNotifierWorker.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => telegram));
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/feedback/new");

        var id = await IdOf(await QueuePost(client, "/feedback", token, Report("Squat suggestion jumped by 10 kg today, which seems far too much for a single week of training")));

        var body = await telegram.WaitAsync();
        telegram.Uri!.AbsoluteUri.Should().Be("http://telegram.test/bot123:secret/sendMessage");
        var text = JsonNode.Parse(body)!["text"]!.GetValue<string>();
        text.Should().StartWith($"Feedback #{id} from Test").And.Contain("Problem · Speed").And.Contain("Squat suggestion jumped by 10 kg");
        text.Should().NotContain("390x844").And.NotContain("boom").And.NotContain("of training", "only the first 80 characters go out");
        JsonNode.Parse(body)!["chat_id"]!.GetValue<string>().Should().Be("42");
    }

    [Fact]
    public async Task Telegram_is_not_called_when_it_is_not_configured()
    {
        var telegram = new RecordingHandler();
        using var app = new AppFactory(fixture, services: s => s.AddHttpClient(FeedbackNotifierWorker.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => telegram));
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/feedback/new");

        await IdOf(await QueuePost(client, "/feedback", token, Report("quiet")));
        await Task.Delay(300);

        telegram.Uri.Should().BeNull();
    }

    private sealed class ThrowingParser : IFoodParser
    {
        public const string Message = "Parser exploded for the error capture test";

        public Task<FoodParseResult> ParseAsync(string text, CancellationToken ct) => throw new InvalidOperationException(Message);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<string> body = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Uri? Uri { get; private set; }

        public async Task<string> WaitAsync() => await body.Task.WaitAsync(TimeSpan.FromSeconds(10));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri;
            body.TrySetResult(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
        }
    }

    [GeneratedRegex("name=\"parseRunId\" value=\"(\\d+)\"")]
    private static partial Regex RunIdField();

    [GeneratedRegex("name=\"clientId\" value=\"([0-9a-f-]+)\"")]
    private static partial Regex ClientIdField();
}
