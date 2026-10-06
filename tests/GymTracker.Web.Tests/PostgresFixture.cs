using GymTracker.Core.Domain;
using GymTracker.Web.Data;
using GymTracker.Web.Data.Seed;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace GymTracker.Web.Tests;

/// <summary>One Postgres 18 container for the whole test run, migrated and seeded once.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18")
        .WithDatabase("gym")
        .WithUsername("gym")
        .WithPassword("gym-test")
        .Build();

    public string ConnectionString => container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        await using var db = CreateContext(null);
        await db.Database.MigrateAsync();
        await ExerciseSeed.SeedAsync(db, CancellationToken.None);
        await UsdaSeed.SeedAsync(db, UsdaSeed.DefaultPath, CancellationToken.None);
        // As at app startup, so matching sees the same shared foods as on the server (parse regression cases).
        await BgFoodSeed.SeedAsync(db, BgFoodSeed.DefaultPath, CancellationToken.None);
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    // Identity builds its tables from IdentityOptions in the application services; give contexts made here the
    // same schema version as the app (Version 3, with passkeys) so the model matches the migrations.
    private static readonly IServiceProvider IdentityServices = new ServiceCollection()
        .Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
        .BuildServiceProvider();

    public AppDbContext CreateContext(string? userId, string? connectionString = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString ?? ConnectionString).UseApplicationServiceProvider(IdentityServices).Options,
            new FixedCurrentUser(userId));

    /// <summary>A new empty database in the shared container, for tests that need their own (migrations, seeding).</summary>
    public async Task<string> NewDatabaseAsync(string name)
    {
        await using (var admin = new NpgsqlConnection(ConnectionString))
        {
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name}", admin);
            await drop.ExecuteNonQueryAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await create.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }

    /// <summary>Inserts a user and profile directly, bypassing Identity, for service-level tests.</summary>
    public async Task<string> CreateUserAsync()
    {
        var id = Guid.NewGuid().ToString();
        await using var db = CreateContext(null);
        db.Users.Add(new AppUser { Id = id, UserName = $"{id}@test", Email = $"{id}@test", NormalizedUserName = $"{id}@TEST", NormalizedEmail = $"{id}@TEST" });
        db.Profiles.Add(new UserProfile { UserId = id, DisplayName = "Test" });
        await db.SaveChangesAsync();
        return id;
    }
}

[CollectionDefinition(Name)]
public class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
