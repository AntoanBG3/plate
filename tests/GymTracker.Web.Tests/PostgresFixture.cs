using GymTracker.Core.Domain;
using GymTracker.Web.Data;
using GymTracker.Web.Data.Seed;
using Microsoft.EntityFrameworkCore;
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
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    public AppDbContext CreateContext(string? userId) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options, new FixedCurrentUser(userId));

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
