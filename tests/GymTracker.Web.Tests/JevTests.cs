using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Integrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace GymTracker.Web.Tests;

public class JevRequestTests
{
    private static readonly MatchQuestion Chicken = new(
        new ParsedFoodItem("chicken breast, skinless", "пилешко филе", 200, FoodState.Cooked, "на скара", null, new Per100g(165, 31, 0, 3.6m)),
        [
            new MatchCandidate(11, "Chicken, breast, roasted", "Пилешко филе, печено", null, new Per100g(165, 31, 0, 3.6m)),
            new MatchCandidate(12, "Chicken, breast, raw", "Пилешко филе, сурово", null, new Per100g(120, 22.5m, 0, 2.6m)),
        ]);

    private static readonly MatchQuestion Rice = new(
        new ParsedFoodItem("white rice", "ориз", 150, FoodState.Cooked, null, null, new Per100g(130, 2.7m, 28, 0.3m)),
        [new MatchCandidate(13, "Rice, white, cooked", null, null, new Per100g(130, 2.7m, 28, 0.3m))]);

    private static JsonNode Answers(string answers) =>
        JsonNode.Parse("""{"model":"typesafe/jev-1.13-20260917","answers":{""" + answers + """},"usage":{"input_tokens":900,"cost":0.00004}}""")!;

    [Fact]
    public void One_request_holds_every_item_with_a_none_option()
    {
        var body = JevFoodMatchDecider.Body(JevFoodMatchDecider.Jev113, "200 г пилешко филе на скара и ориз", [Chicken, Rice]);

        body["model"]!.GetValue<string>().Should().Be("typesafe/jev-1.13");
        body["state"]!["diary_entry"]!.GetValue<string>().Should().Be("200 г пилешко филе на скара и ориз");
        var food = body["state"]!["foods"]!["i0"]!;
        food["as_written"]!.GetValue<string>().Should().Be("пилешко филе");
        food["english"]!.GetValue<string>().Should().Be("chicken breast, skinless");
        food["state"]!.GetValue<string>().Should().Be("cooked");
        food["grams"]!.GetValue<decimal>().Should().Be(200);

        var questions = body["questions"]!.AsObject();
        questions.Select(q => q.Key).Should().Equal("i0", "i1");
        questions["i0"]!["type"]!.GetValue<string>().Should().Be("choice");
        questions["i0"]!["instructions"]!.GetValue<string>().Should().Contain("foods.i0");
        var criteria = questions["i0"]!["criteria"]!.AsObject();
        criteria.Select(c => c.Key).Should().Equal("f11", "f12", "none");
        criteria["f11"]!.GetValue<string>().Should().Contain("Chicken, breast, roasted").And.Contain("Пилешко филе, печено").And.Contain("165 kcal");
        questions["i1"]!["criteria"]!["f13"]!.GetValue<string>().Should().NotContain(" / ");
    }

    [Fact]
    public void Items_without_candidates_get_no_question()
    {
        var body = JevFoodMatchDecider.Body(JevFoodMatchDecider.Jev113, "chicken and zzqx", [Chicken, Rice with { Candidates = [] }]);

        body["questions"]!.AsObject().Select(q => q.Key).Should().Equal("i0");
        body["state"]!["foods"]!["i1"].Should().NotBeNull("the item is still context for the others");
    }

    [Fact]
    public void Picks_none_and_confidence_are_read_per_item()
    {
        var result = JevFoodMatchDecider.Read(Answers("""
            "i0":{"type":"choice","choice":"f12","confidence":0.91,"probabilities":{"f11":0.04,"f12":0.95,"none":0.01}},
            "i1":{"type":"choice","choice":"none","confidence":0.7,"probabilities":{"f13":0.2,"none":0.8}}
            """), [Chicken, Rice])!;

        result.Model.Should().Be("typesafe/jev-1.13-20260917");
        result.Items.Should().Equal(new MatchDecision(12, 0.91m), new MatchDecision(null, 0.7m));
    }

    [Fact]
    public void Unknown_or_missing_answers_leave_that_item_undecided()
    {
        var result = JevFoodMatchDecider.Read(Answers("""
            "i0":{"type":"choice","choice":"f999","confidence":0.99,"probabilities":{}}
            """), [Chicken, Rice])!;

        result.Items.Should().HaveCount(2).And.OnlyContain(d => d == null);
        JevFoodMatchDecider.Read(JsonNode.Parse("""{"error":{"code":500}}"""), [Chicken, Rice]).Should().BeNull();
    }
}

/// <summary>A fake Decisions API: answers every request with a fixed status and body, optionally after a delay.</summary>
internal sealed class StubJev(HttpStatusCode status = HttpStatusCode.OK, string? body = null, TimeSpan? delay = null) : HttpMessageHandler
{
    public const string BananaPick = """
        {"model":"typesafe/jev-1.13-20260917","answers":{"i0":{"type":"choice","choice":"f1","confidence":0.97,"probabilities":{"f1":0.98,"f2":0.01,"none":0.01}}},"usage":{"input_tokens":300,"cost":0.0000126}}
        """;

    public int Calls { get; private set; }
    public Uri? SentUri { get; private set; }
    public string? SentKey { get; private set; }
    public JsonNode? SentBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        SentUri = request.RequestUri;
        SentKey = request.Headers.Authorization?.Parameter;
        SentBody = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
        if (delay is { } d)
        {
            await Task.Delay(d, ct);
        }

        return new HttpResponseMessage(status) { Content = new StringContent(body ?? BananaPick, System.Text.Encoding.UTF8, "application/json") };
    }

    public JevFoodMatchDecider Decider() =>
        new(new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri("https://openrouter.test/api/v1/") }, NullLogger<JevFoodMatchDecider>.Instance);
}

public class JevCallTests
{
    private static readonly MatchQuestion Banana = new(
        new ParsedFoodItem("banana", "банан", 120, FoodState.NotApplicable, null, null, new Per100g(89, 1.1m, 23, 0.3m)),
        [new MatchCandidate(1, "Bananas, raw", "Банани", null, new Per100g(89, 1.1m, 23, 0.3m)), new MatchCandidate(2, "Pepper, banana, raw", null, null, new Per100g(27, 1.7m, 5.4m, 0.5m))]);

    [Fact]
    public async Task Posts_to_the_decisions_endpoint_with_the_key()
    {
        var stub = new StubJev();

        var result = await stub.Decider().DecideAsync("a banana", [Banana], "sk-or-v1-key", JevFoodMatchDecider.Jev113, default);

        stub.SentUri!.AbsolutePath.Should().Be("/api/alpha/decisions");
        stub.SentKey.Should().Be("sk-or-v1-key");
        stub.SentBody!["model"]!.GetValue<string>().Should().Be(JevFoodMatchDecider.Jev113);
        result!.Items.Should().Equal(new MatchDecision(1, 0.97m));
        result.Model.Should().Be("typesafe/jev-1.13-20260917");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Http_errors_return_null_without_retrying(HttpStatusCode status)
    {
        var stub = new StubJev(status, """{"error":{"code":500,"message":"nope"}}""");

        (await stub.Decider().DecideAsync("a banana", [Banana], "sk-or-v1-key", JevFoodMatchDecider.Jev113, default)).Should().BeNull();
        stub.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_slow_answer_returns_null_after_the_timeout()
    {
        var stub = new StubJev(delay: TimeSpan.FromSeconds(2));
        var decider = stub.Decider();
        decider.Timeout = TimeSpan.FromMilliseconds(200);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        (await decider.DecideAsync("a banana", [Banana], "sk-or-v1-key", JevFoodMatchDecider.Jev113, default)).Should().BeNull();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Malformed_json_returns_null()
    {
        var stub = new StubJev(body: "<html>Bad gateway</html>");

        (await stub.Decider().DecideAsync("a banana", [Banana], "sk-or-v1-key", JevFoodMatchDecider.Jev113, default)).Should().BeNull();
    }

    [Fact]
    public async Task The_test_decision_reports_what_went_wrong()
    {
        (await new StubJev().Decider().TestAsync("sk-or-v1-key", JevFoodMatchDecider.Jev113, default)).Should().BeNull();

        var rejected = await new StubJev(HttpStatusCode.Unauthorized, """{"error":{"code":401}}""").Decider().TestAsync("sk-or-v1-key", JevFoodMatchDecider.Jev113, default);
        rejected!.ToString().Should().Contain("rejected the API key (401)");

        var wrong = await new StubJev(body: StubJev.BananaPick.Replace("\"f1\",\"confidence\"", "\"f2\",\"confidence\"")).Decider().TestAsync("sk-or-v1-key", JevFoodMatchDecider.Jev113, default);
        wrong.Should().NotBeNull("the matcher picked the banana pepper for a banana");
    }
}
