using FluentAssertions;
using GymTracker.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GymTracker.Web.Tests;

public class ConnectionStringTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task The_database_connection_never_probes_for_Kerberos()
    {
        // The chiseled runtime image has no libgssapi_krb5; probing for GSS encryption logged an error on every start.
        await using var app = new AppFactory(fixture);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()).GssEncryptionMode.Should().Be(GssEncryptionMode.Disable);
        (await db.Database.CanConnectAsync()).Should().BeTrue("the connection still works");
    }
}
