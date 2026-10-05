using FluentAssertions;
using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace GymTracker.Web.Tests;

/// <summary>Data changes inside migrations, run against a separate database in the shared container.</summary>
[Collection(PostgresCollection.Name)]
public class MigrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Auto_protein_turns_the_old_default_into_auto_and_keeps_chosen_values()
    {
        var connection = await NewDatabaseAsync("auto_protein");
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

    private async Task<string> NewDatabaseAsync(string name)
    {
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name}", admin);
            await drop.ExecuteNonQueryAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await create.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
    }
}
