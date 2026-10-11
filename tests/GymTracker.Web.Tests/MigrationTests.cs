using FluentAssertions;
using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace GymTracker.Web.Tests;

/// <summary>Data changes inside migrations, run against a separate database in the shared container.</summary>
public class MigrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Auto_protein_turns_the_old_default_into_auto_and_keeps_chosen_values()
    {
        var connection = await fixture.NewDatabaseAsync("auto_protein");
        await using (var db = fixture.CreateContext(null, connection))
        {
            await db.GetService<IMigrator>().MigrateAsync("AddOnboardingChat");
            await AddUserAsync(db, "default", 2.0m);
            await AddUserAsync(db, "chosen", 2.2m);
        }

        await using (var db = fixture.CreateContext(null, connection))
        {
            await db.Database.MigrateAsync();
            var protein = await db.Profiles.IgnoreQueryFilters().ToDictionaryAsync(p => p.UserId, p => p.ProteinGramsPerKg);

            protein["default"].Should().BeNull();
            protein["chosen"].Should().Be(2.2m);
        }
    }

    [Fact]
    public async Task Rolling_back_label_scans_keeps_shared_barcodes_and_clears_colliding_private_ones()
    {
        // Phase 13 lets private foods repeat a barcode; the older schema had one barcode per food overall.
        var connection = await fixture.NewDatabaseAsync("label_rollback");
        await using (var db = fixture.CreateContext(null, connection))
        {
            await db.Database.MigrateAsync();
            db.Users.AddRange(
                new AppUser { Id = "someone", UserName = "someone@test", Email = "someone@test" },
                new AppUser { Id = "someone else", UserName = "else@test", Email = "else@test" });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "FoodItems" ("Name", "Barcode", "OwnerUserId", "KcalPer100g", "ProteinPer100g", "CarbsPer100g", "FatPer100g", "Source")
                VALUES ('shared', '5901234123457', NULL, 1, 0, 0, 0, 'Manual'),
                       ('private copy', '5901234123457', 'someone', 1, 0, 0, 0, 'Manual'),
                       ('two private a', '4006381333931', 'someone', 1, 0, 0, 0, 'Manual'),
                       ('two private b', '4006381333931', 'someone else', 1, 0, 0, 0, 'Manual'),
                       ('alone', '96385074', 'someone', 1, 0, 0, 0, 'Manual');
                """);

            await db.GetService<IMigrator>().MigrateAsync("AddInvites");

            var barcodes = await db.Database.SqlQueryRaw<NameAndBarcode>("""SELECT "Name", "Barcode" FROM "FoodItems" WHERE "Name" IN ('shared', 'private copy', 'two private a', 'two private b', 'alone')""").ToListAsync();
            barcodes.Should().BeEquivalentTo([
                new NameAndBarcode("shared", "5901234123457"),
                new NameAndBarcode("private copy", null),
                new NameAndBarcode("two private a", null),
                new NameAndBarcode("two private b", null),
                new NameAndBarcode("alone", "96385074"),
            ]);

            await db.Database.MigrateAsync();
        }
    }

    private sealed record NameAndBarcode(string Name, string? Barcode);

    /// <summary>Plain SQL, since the current model has columns the old schema lacks.</summary>
    private static async Task AddUserAsync(AppDbContext db, string id, decimal proteinPerKg)
    {
        db.Users.Add(new AppUser { Id = id, UserName = $"{id}@test", Email = $"{id}@test", NormalizedUserName = $"{id}@TEST", NormalizedEmail = $"{id}@TEST" });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Profiles" ("UserId", "DisplayName", "TimeZoneId", "Goal", "TargetRatePercentPerWeek", "ProteinGramsPerKg", "DefaultWeightIncrementKg")
            VALUES ({id}, {id}, 'Europe/Sofia', 'Maintain', 0, {proteinPerKg}, 2.5)
            """);
    }

    [Fact]
    public async Task Rolling_back_label_photo_slots_keeps_the_first_photo()
    {
        var connection = await fixture.NewDatabaseAsync("label_slots_rollback");
        await using var db = fixture.CreateContext(null, connection);
        await db.Database.MigrateAsync();
        db.Users.Add(new AppUser { Id = "someone", UserName = "someone@test", Email = "someone@test" });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "LabelScans" ("Id", "UserId", "CreatedAt", "Model", "LatencyMs", "Edited") VALUES (1, 'someone', now(), 'test/label', 1, false);
            INSERT INTO "LabelScanPhotos" ("LabelScanId", "Position", "ContentType", "Width", "Height", "Bytes")
            VALUES (1, 0, 'image/jpeg', 1, 1, '\x01'), (1, 1, 'image/jpeg', 1, 1, '\x02'), (1, 2, 'image/jpeg', 1, 1, '\x03');
            """);

        await db.GetService<IMigrator>().MigrateAsync("UnnamedFoods");

        var left = await db.Database.SqlQueryRaw<int>("""SELECT COUNT(*)::int AS "Value" FROM "LabelScanPhotos" WHERE "LabelScanId" = 1""").SingleAsync();
        left.Should().Be(1);
    }
}
