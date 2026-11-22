using System.Net;
using FluentAssertions;
using Plate.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Plate.Web.Tests;

/// <summary>The recipe editor's pages and endpoints, over HTTP (SPEC 7 Food, 5.12).</summary>
public class RecipePageTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Editor = "/food/recipes/new?date=2026-11-20&meal=Dinner";

    /// <summary>A plain form post, as the Save button sends it (no htmx).</summary>
    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, string path, string token, IEnumerable<KeyValuePair<string, string>> fields) =>
        client.PostAsync(path, new FormUrlEncodedContent(fields.Append(new("__RequestVerificationToken", token))));

    private static IEnumerable<KeyValuePair<string, string>> Recipe(string name, string grams = "300", string yield = "") =>
    [
        new("date", "2026-11-20"), new("meal", "Dinner"), new("name", name), new("yieldGrams", yield),
        // Keys in any order, as rows added and removed in the editor leave them.
        new("items.Index", "b7"), new("items[b7].Include", "true"), new("items[b7].Description", "Леща"), new("items[b7].Grams", grams),
        new("items[b7].Kcal", "352"), new("items[b7].Protein", "25"), new("items[b7].Carbs", "60"), new("items[b7].Fat", "1"),
        new("items.Index", "a1"), new("items[a1].Include", "true"), new("items[a1].Description", "Морков"), new("items[a1].Grams", "200"),
        new("items[a1].Kcal", "41"), new("items[a1].Protein", "0.9"), new("items[a1].Carbs", "10"), new("items[a1].Fat", "0.2"),
        new("items.Index", "zz"), new("items[zz].Include", "false"), new("items[zz].Description", "Unticked"), new("items[zz].Grams", "100"),
        new("items[zz].Kcal", "900"), new("items[zz].Protein", "0"), new("items[zz].Carbs", "0"), new("items[zz].Fat", "100"),
    ];

    [Fact]
    public async Task A_recipe_saved_from_the_editor_is_on_the_Recent_tab_and_opens_for_editing()
    {
        await using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, Editor);

        var saved = await SubmitAsync(client, "/food/recipes", token, Recipe("Леща яхния", yield: "1000"));

        AppFactory.IsRedirectTo(saved, "/food/add?date=2026-11-20&meal=Dinner&tab=recent").Should().BeTrue();
        var recent = WebUtility.HtmlDecode(await client.GetStringAsync("/food/add?date=2026-11-20&meal=Dinner&tab=recent"));
        recent.Should().Contain("Леща яхния").And.Contain("114 kcal, 7.7 g protein per 100 g").And.Contain("2 ingredients", "the unticked row isn't saved");
        var id = System.Text.RegularExpressions.Regex.Match(recent, @"/food/recipes/(\d+)\?").Groups[1].Value;
        var editor = WebUtility.HtmlDecode(await client.GetStringAsync($"/food/recipes/{id}?date=2026-11-20&meal=Dinner"));
        editor.Should().Contain("Edit recipe").And.Contain("Леща").And.Contain("Морков").And.MatchRegex("name=\"yieldGrams\" value=\"1000\"");
    }

    [Fact]
    public async Task A_refused_save_comes_back_with_the_rows_and_what_to_fix()
    {
        await using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, Editor);

        var refused = await SubmitAsync(client, "/food/recipes", token, Recipe("Too light", yield: "50"));

        refused.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync());
        html.Should().Contain("The cooked weight should be between 100 and 1500 g for these ingredients.").And.Contain("Леща").And.Contain("Морков").And.NotContain("Unticked");
    }

    [Fact]
    public async Task Another_account_gets_a_404_for_a_recipe_it_does_not_own()
    {
        await using var app = new AppFactory(fixture);
        var (owner, _) = await app.SignedInClientAsync();
        await SubmitAsync(owner, "/food/recipes", await AppFactory.TokenAsync(owner, Editor), Recipe("Private dish"));
        int id;
        await using (var db = fixture.CreateContext(null))
        {
            id = await db.FoodItems.IgnoreQueryFilters().Where(f => f.Name == "Private dish" && f.Source == FoodSource.Recipe).Select(f => f.Id).SingleAsync();
        }

        var (other, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(other, Editor);
        (await other.GetAsync($"/food/recipes/{id}?date=2026-11-20&meal=Dinner")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SubmitAsync(other, $"/food/recipes/{id}", token, Recipe("Taken"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SubmitAsync(other, $"/food/recipes/{id}/delete", token, [new("date", "2026-11-20"), new("meal", "Dinner")])).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AppFactory.PostAsync(other, "/food/log-item", token, new() { ["date"] = "2026-11-20", ["meal"] = "Dinner", ["foodItemId"] = id.ToString(), ["grams"] = "300", ["source"] = "Recent" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "it can't be logged either");
    }

    [Fact]
    public async Task Typed_ingredients_come_back_as_rows_with_their_own_keys()
    {
        await using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, Editor);

        var rows = await AppFactory.PostAsync(client, "/food/recipes/parse", token, new() { ["text"] = "200 g rice, 400 g chicken breast" });

        rows.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await rows.Content.ReadAsStringAsync());
        var keys = System.Text.RegularExpressions.Regex.Matches(html, "name=\"items.Index\" value=\"([0-9a-f]{8})\"").Select(m => m.Groups[1].Value).ToList();
        keys.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        html.Should().Contain($"name=\"items[{keys[0]}].Grams\"");
        (await AppFactory.PostAsync(client, "/food/recipes/parse", token, new() { ["text"] = "" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
