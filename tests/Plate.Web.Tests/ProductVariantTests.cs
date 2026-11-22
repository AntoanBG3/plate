using FluentAssertions;
using Plate.Core.Domain;
using Plate.Core.Nutrition;
using Plate.Web.Features.Food;

namespace Plate.Web.Tests;

public class ProductVariantTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Brand_in_its_own_column_finds_only_the_visible_product_of_the_named_type()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var other = await fixture.CreateUserAsync();
        var wanted = new FoodItem { Name = "Skyr", Brand = "Meadow Mountain Dairy", Source = FoodSource.Manual };
        s.Db.FoodItems.AddRange(wanted,
            new FoodItem { Name = "Meadow Mountain Dairy", Brand = "Other", NameBg = "Лимонада", Source = FoodSource.Manual },
            new FoodItem { Name = "Lemonade", Brand = wanted.Brand, Source = FoodSource.Manual },
            new FoodItem { Name = "Meadow Mountain Dairy skyr", OwnerUserId = other, Source = FoodSource.Manual });
        await s.Db.SaveChangesAsync();
        s.MatchDecider.Enabled = false;
        var name = "Meadow Mountain Dairy skyr";
        var row = (await s.Matcher.ResolveAsync(name, [new ParsedFoodItem(name, name, 150, FoodState.NotApplicable, null, null, new Per100g(90, 8, 4, 1))], default)).Single();
        row.FoodItemId.Should().Be(wanted.Id);
    }

    [Fact]
    public async Task The_cola_product_alias_excludes_other_drinks_from_the_manufacturer()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        s.MatchDecider.Enabled = false;
        var item = new ParsedFoodItem("Coca-Cola", "Coca-Cola", 330, FoodState.NotApplicable, null, null, new Per100g(42, 0, 10, 0));
        var result = (await s.Matcher.ResolveAsync("Coca-Cola", [item], default)).Single();
        result.MatchedName.Should().ContainEquivalentOf("cola");
        result.Per100.Kcal.Should().BeInRange(35, 48);
        result.MatchedName.Should().NotContainEquivalentOf("lemonade");
    }

    [Fact]
    public async Task After_an_unsure_none_plain_tofu_is_not_fried_tofu()
    {
        // Matchbench c48: the production decider said "none" at 0.36 for "150g tofu", and the ranking's fried tofu stood.
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        s.MatchDecider.Answer = q => StubMatchDecider.First(q, null, 0.36m);
        var item = new ParsedFoodItem("tofu", "tofu", 150, FoodState.NotApplicable, null, null, new Per100g(80, 8, 2, 5));

        var row = (await s.Matcher.ResolveAsync("150g tofu", [item], default)).Single();

        row.FoodItemId.Should().NotBeNull();
        (row.MatchedNameEn ?? row.MatchedName).Should().ContainEquivalentOf("tofu").And.NotContainEquivalentOf("fried");
    }

    [Theory]
    [InlineData("Beverages, The COCA-COLA company, Minute Maid, Lemonade", "cola", true)]
    [InlineData("Beverages, COCA-COLA, POWERADE, lemon-lime flavored, ready-to-drink", "cola", true)]
    [InlineData("Beverages, carbonated, cola, regular", "cola", false)]
    [InlineData("Candies, MARS SNACKFOOD US, SNICKERS CRUNCHER", "snickers", false)]
    public void A_food_named_only_in_its_maker_field_is_not_that_food(string name, string query, bool onlyMaker) =>
        ProductVariants.MatchesOnlyItsMaker(name, query).Should().Be(onlyMaker);

    [Theory]
    [InlineData("tofu", "Tofu, prepared with calcium sulfate", "Tofu, fried")]
    [InlineData("fried tofu", "Tofu, fried", "Tofu, prepared with calcium sulfate")]
    [InlineData("кисело мляко 2%", "Кисело мляко, 2% масленост", "Кисело мляко 4%")]
    [InlineData("vanilla skyr", "Skyr, vanilla flavour", "Skyr, strawberry")]
    public void Requested_variant_outranks_the_alternative(string query, string wanted, string other)
    {
        static FoodMatch Match(string name) => new(1, name, name, null, 100, 0, 0, 0, null, null, null, false, false, 1);
        FoodMatcher.Rank(Match(wanted), query, FoodState.NotApplicable)
            .Should().BeGreaterThan(FoodMatcher.Rank(Match(other), query, FoodState.NotApplicable));
    }
}
