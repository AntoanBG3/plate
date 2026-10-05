using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Web.Data.Seed;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

/// <summary>Stored food names with a look-alike letter from the other alphabet are fixed once (Phase 11).</summary>
[Collection(PostgresCollection.Name)]
public class MixedScriptFixupTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Mixed_names_are_fixed_and_the_seed_does_not_add_them_again()
    {
        await using var db = fixture.CreateContext(null);
        var food = new FoodItem { Name = "Картофен чипc Test", NameBg = "Картофен чипc Test", Brand = "Мousse Co", Source = FoodSource.Reference, KcalPer100g = 500, ProteinPer100g = 5, CarbsPer100g = 50, FatPer100g = 30 };
        db.FoodItems.Add(food);
        await db.SaveChangesAsync();
        var before = await db.FoodItems.IgnoreQueryFilters().CountAsync();

        await DataFixups.RunAsync(db, default);
        await BgFoodSeed.SeedAsync(db, BgFoodSeed.DefaultPath, default);

        db.ChangeTracker.Clear();
        var fixedFood = await db.FoodItems.IgnoreQueryFilters().SingleAsync(f => f.Id == food.Id);
        (fixedFood.Name, fixedFood.NameBg, fixedFood.Brand).Should().Be(("Картофен чипс Test", "Картофен чипс Test", "Mousse Co"));
        (await db.FoodItems.IgnoreQueryFilters().CountAsync()).Should().Be(before, "the seed found every food already there");
        (await db.FoodItems.IgnoreQueryFilters().CountAsync(f => f.Name == "Mousse strawberry banana apple carrot" || f.Name == "Мousse strawberry banana apple carrot"))
            .Should().Be(1);
    }
}
