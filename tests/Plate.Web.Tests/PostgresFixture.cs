using Plate.Core.Domain;
using Plate.Web.Data;
using Plate.Web.Data.Seed;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Plate.Web.Tests;

/// <summary>
/// A database of its own for each test class (or E2E fixture), so classes can run in parallel without seeing each
/// other's rows. One Postgres 18 container serves the whole test run: the first fixture migrates and seeds the
/// <c>gym</c> database, and every fixture gets a copy of it made with CREATE DATABASE ... TEMPLATE.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Started once per test process; Testcontainers' reaper removes the container when the process ends.
    private static readonly Lazy<Task<PostgreSqlContainer>> Shared = new(StartAsync);

    // One copy at a time: CREATE DATABASE fails while anything else is using the template.
    private static readonly SemaphoreSlim Copying = new(1, 1);

    private static async Task<PostgreSqlContainer> StartAsync()
    {
        var container = new PostgreSqlBuilder("postgres:18")
            .WithDatabase("gym")
            .WithUsername("gym")
            .WithPassword("gym-test")
            // A throwaway database: skip waiting for disk on every commit. Room for every class's connection pool.
            .WithCommand("-c", "fsync=off", "-c", "synchronous_commit=off", "-c", "full_page_writes=off", "-c", "max_connections=500")
            .Build();
        await container.StartAsync();
        await using (var db = Context(container.GetConnectionString(), null))
        {
            await db.Database.MigrateAsync();
            await ExerciseSeed.SeedAsync(db, CancellationToken.None);
            await UsdaSeed.SeedAsync(db, UsdaSeed.DefaultPath, CancellationToken.None);
            // As at app startup, so matching sees the same shared foods as on the server (parse regression cases).
            await BgFoodSeed.SeedAsync(db, BgFoodSeed.DefaultPath, CancellationToken.None);
        }

        // Pooled connections to the template would make every copy fail.
        NpgsqlConnection.ClearAllPools();
        return container;
    }

    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var container = await Shared.Value;
        var name = $"t_{Guid.NewGuid():N}";
        var server = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = "postgres" }.ConnectionString;
        await Copying.WaitAsync();
        try
        {
            await using var admin = new NpgsqlConnection(server);
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name} TEMPLATE gym", admin);
            await create.ExecuteNonQueryAsync();
        }
        finally
        {
            Copying.Release();
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = name }.ConnectionString;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // Identity builds its tables from IdentityOptions in the application services; give contexts made here the
    // same schema version as the app (Version 3, with passkeys) so the model matches the migrations.
    private static readonly IServiceProvider IdentityServices = new ServiceCollection()
        .Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
        .BuildServiceProvider();

    public AppDbContext CreateContext(string? userId, string? connectionString = null, params IInterceptor[] interceptors) => Context(connectionString ?? ConnectionString, userId, interceptors);

    private static AppDbContext Context(string connectionString, string? userId, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).UseApplicationServiceProvider(IdentityServices).AddInterceptors(interceptors).Options,
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
