using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Onboarding;
using GymTracker.Web.Integrations;

namespace GymTracker.Web.Tests;

public class OnboardingAssistantTests
{
    private static string Completion(object turn) =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = JsonSerializer.Serialize(turn) } } } });

    private static object Fields(
        string? name = null, string? sex = null, int? birthYear = null, double? heightCm = null, double? weightKg = null,
        string? activityLevel = null, string? goal = null, double? ratePercentPerWeek = null) => new
    {
        name, sex, birthYear, heightCm, weightKg, activityLevel, goal, ratePercentPerWeek,
        proteinGramsPerKg = (double?)null, startingMaintenanceKcal = (int?)null,
    };

    private static OnboardingContext Context(OnboardingDraft? draft = null, IReadOnlyList<OnboardingRejection>? rejected = null, bool bulgarian = false)
    {
        draft ??= new OnboardingDraft { Name = "Ivan" };
        return new OnboardingContext(draft, draft.Missing(), rejected ?? [], [new ChatLine(false, "Hi Ivan!"), new ChatLine(true, "I'm 30, 6 ft and 180 lb")], bulgarian, 2026);
    }

    [Fact]
    public async Task Reads_the_reply_values_and_quick_replies()
    {
        var stub = new StubOpenRouter(completion: Completion(new
        {
            reply = "Thanks! Do you sit most of the day?",
            quickReplies = new[] { "Desk job", "On my feet", "Desk job", "Manual work", "Athlete", "Something else entirely" },
            fields = Fields(sex: "male", birthYear: 1996, heightCm: 182.88, weightKg: 81.65, activityLevel: "low_active", goal: "cut", ratePercentPerWeek: 0.5),
        }));

        var turn = await stub.Onboarding().NextAsync(Context(), "key", "google/gemini-3.5-flash-lite", default);

        turn.Error.Should().BeNull();
        turn.Reply.Should().Be("Thanks! Do you sit most of the day?");
        turn.Model.Should().Be("google/gemini-3.5-flash-lite");
        turn.QuickReplies.Should().Equal("Desk job", "On my feet", "Manual work", "Athlete");
        turn.Fields.Should().Be(new OnboardingFields(
            Sex: Sex.Male, BirthYear: 1996, HeightCm: 183, WeightKg: 81.65m, ActivityLevel: ActivityLevel.LowActive, Goal: Goal.Cut, RatePercentPerWeek: 0.5m));
    }

    [Fact]
    public async Task Sends_the_draft_missing_fields_refusals_and_history_with_strict_output()
    {
        var stub = new StubOpenRouter(completion: Completion(new { reply = "Hi", quickReplies = Array.Empty<string>(), fields = Fields() }));
        var draft = new OnboardingDraft { Name = "Ivan", Sex = Sex.Male };
        var rejected = new[] { new OnboardingRejection(OnboardingField.Height, "Height must be between 120 and 230 cm.") };

        await stub.Onboarding().NextAsync(Context(draft, rejected, bulgarian: true), "key", "google/gemini-3.5-flash-lite", default);

        var body = stub.SentBody!;
        body["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>().Should().BeTrue();
        body["provider"]!["require_parameters"]!.GetValue<bool>().Should().BeTrue();
        var messages = body["messages"]!.AsArray();
        messages.Select(m => m!["role"]!.GetValue<string>()).Should().Equal("system", "assistant", "user");
        var system = messages[0]!["content"]!.GetValue<string>();
        system.Should().Contain("\"name\":\"Ivan\"").And.Contain("\"sex\":\"male\"");
        system.Should().Contain("Still missing: birthYear (age), heightCm, weightKg, activityLevel, goal");
        system.Should().Contain("Refused from the last answer, ask again: heightCm: Height must be between 120 and 230 cm.");
        system.Should().Contain("Bulgarian").And.Contain("age 18 to 100 (born 1926 to 2008)");
        system.Should().Contain("low_active: About 7,500 to 12,000 steps a day");
    }

    [Fact]
    public async Task The_first_turn_asks_for_a_greeting()
    {
        var stub = new StubOpenRouter(completion: Completion(new { reply = "Hi", quickReplies = Array.Empty<string>(), fields = Fields() }));
        var draft = new OnboardingDraft();

        await stub.Onboarding().NextAsync(new OnboardingContext(draft, draft.Missing(), [], [], false, 2026), "key", "m", default);

        var messages = stub.SentBody!["messages"]!.AsArray();
        messages.Should().HaveCount(2);
        messages[1]!["content"]!.GetValue<string>().Should().Contain("just opened the setup chat");
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("{\"reply\": \"\"}")]
    public void An_unreadable_answer_is_an_error(string content)
    {
        var response = JsonNode.Parse(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }));

        var turn = OpenRouterOnboardingAssistant.Read(response, "m");

        turn.Reply.Should().BeNull();
        turn.Error.Should().NotBeNull();
    }

    [Fact]
    public void Unknown_values_become_null_without_failing_the_turn()
    {
        var response = JsonNode.Parse(Completion(new
        {
            reply = "Ok",
            quickReplies = Array.Empty<string>(),
            fields = Fields(sex: "other", activityLevel: "couch", goal: "recomp", heightCm: double.MaxValue),
        }));

        var turn = OpenRouterOnboardingAssistant.Read(response, "m");

        turn.Reply.Should().Be("Ok");
        turn.Fields.Should().Be(OnboardingFields.None);
    }

    [Fact]
    public async Task A_rejected_key_is_reported_without_retrying()
    {
        var turn = await new StubOpenRouter(HttpStatusCode.Unauthorized, "{}").Onboarding().NextAsync(Context(), "bad", "m", default);

        turn.Reply.Should().BeNull();
        turn.Error!.Format.Should().Be("The assistant isn't available right now. Use the form instead.");
    }
}
