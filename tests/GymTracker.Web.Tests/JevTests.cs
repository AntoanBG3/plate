using System.Text.Json.Nodes;
using FluentAssertions;
using GymTracker.Core.Nutrition;
using GymTracker.Web.Integrations;

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
