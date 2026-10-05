using FluentAssertions;
using GymTracker.Core.Domain;
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
            AddUser(db, "default", 2.0m);
            AddUser(db, "chosen", 2.2m);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext(null, connection))
        {
            await db.Database.MigrateAsync();
            var protein = await db.Profiles.IgnoreQueryFilters().ToDictionaryAsync(p => p.UserId, p => p.ProteinGramsPerKg);

            protein["default"].Should().BeNull();
            protein["chosen"].Should().Be(2.2m);
        }
    }

    private static void AddUser(AppDbContext db, string id, decimal proteinPerKg)
    {
        db.Users.Add(new AppUser { Id = id, UserName = $"{id}@test", Email = $"{id}@test", NormalizedUserName = $"{id}@TEST", NormalizedEmail = $"{id}@TEST" });
        db.Profiles.Add(new UserProfile { UserId = id, DisplayName = id, ProteinGramsPerKg = proteinPerKg });
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
