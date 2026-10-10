using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Web.Data.Migrations;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

/// <summary>The barcode indexes, label scan ownership and the AddedBy backfill (SPEC 4, Phase 13).</summary>
[Collection(PostgresCollection.Name)]
public class LabelDataTests(PostgresFixture fixture)
{
    /// <summary>A barcode no other test uses, so the unique indexes only see this test's rows.</summary>
    private static string NewBarcode() => Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString();

    private static FoodItem Food(string? owner, string barcode, string? addedBy = null) => new()
    {
        OwnerUserId = owner,
        AddedByUserId = addedBy ?? owner,
        Name = "Test yogurt",
        Barcode = barcode,
        KcalPer100g = 62,
        ProteinPer100g = 3.2m,
        CarbsPer100g = 4.1m,
        FatPer100g = 3.6m,
        Source = FoodSource.Manual,
    };

    [Fact]
    public async Task A_private_barcode_does_not_block_another_users_private_one()
    {
        var (a, b) = (await fixture.CreateUserAsync(), await fixture.CreateUserAsync());
        var code = NewBarcode();
        await using var db = fixture.CreateContext(null);
        db.FoodItems.AddRange(Food(a, code), Food(b, code));
        await db.Invoking(d => d.SaveChangesAsync()).Should().NotThrowAsync();
    }

    [Fact]
    public async Task Two_shared_foods_cannot_have_the_same_barcode()
    {
        var a = await fixture.CreateUserAsync();
        var code = NewBarcode();
        await using var db = fixture.CreateContext(null);
        db.FoodItems.Add(Food(null, code, a));
        await db.SaveChangesAsync();

        await using var other = fixture.CreateContext(null);
        other.FoodItems.Add(Food(null, code, a));
        await other.Invoking(d => d.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task One_user_cannot_have_two_private_foods_with_the_same_barcode()
    {
        var a = await fixture.CreateUserAsync();
        var code = NewBarcode();
        await using var db = fixture.CreateContext(null);
        db.FoodItems.Add(Food(a, code));
        await db.SaveChangesAsync();

        await using var other = fixture.CreateContext(null);
        other.FoodItems.Add(Food(a, code));
        await other.Invoking(d => d.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task A_private_and_a_shared_food_may_have_the_same_barcode()
    {
        var a = await fixture.CreateUserAsync();
        var code = NewBarcode();
        await using var db = fixture.CreateContext(null);
        db.FoodItems.AddRange(Food(a, code), Food(null, code, a));
        await db.Invoking(d => d.SaveChangesAsync()).Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_label_scan_and_its_photo_are_invisible_to_other_users()
    {
        var (a, b) = (await fixture.CreateUserAsync(), await fixture.CreateUserAsync());
        await using (var mine = fixture.CreateContext(a))
        {
            mine.LabelScans.Add(new LabelScan
            {
                UserId = a,
                CreatedAt = DateTimeOffset.UtcNow,
                Model = "test/label",
                Photos = [new LabelScanPhoto { ContentType = "image/jpeg", Width = 10, Height = 10, Bytes = [0xFF, 0xD8, 0xFF] }],
            });
            await mine.SaveChangesAsync();
            (await mine.LabelScans.CountAsync()).Should().Be(1);
            (await mine.LabelScanPhotos.CountAsync()).Should().Be(1);
        }

        await using var theirs = fixture.CreateContext(b);
        (await theirs.LabelScans.CountAsync()).Should().Be(0);
        (await theirs.LabelScanPhotos.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_backfill_credits_whoever_logged_a_shared_manual_food_first()
    {
        var (first, second) = (await fixture.CreateUserAsync(), await fixture.CreateUserAsync());
        await using var db = fixture.CreateContext(null);
        var shared = Food(null, NewBarcode());
        shared.AddedByUserId = null;
        var unlogged = Food(null, NewBarcode());
        unlogged.AddedByUserId = null;
        db.FoodItems.AddRange(shared, unlogged);
        await db.SaveChangesAsync();

        var day = new DateOnly(2026, 10, 1);
        db.FoodLog.AddRange(
            new FoodLogEntry { UserId = second, Date = day, Description = "Yogurt", Grams = 100, FoodItemId = shared.Id, CreatedAt = DateTimeOffset.UtcNow },
            new FoodLogEntry { UserId = first, Date = day, Description = "Yogurt", Grams = 100, FoodItemId = shared.Id, CreatedAt = DateTimeOffset.UtcNow.AddDays(-1) });
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(AddLabelScansAndSharedBy.BackfillAddedBy);

        await using var check = fixture.CreateContext(null);
        (await check.FoodItems.IgnoreQueryFilters().SingleAsync(f => f.Id == shared.Id)).AddedByUserId.Should().Be(first);
        (await check.FoodItems.IgnoreQueryFilters().SingleAsync(f => f.Id == unlogged.Id)).AddedByUserId.Should().BeNull();
    }
}
