using FluentAssertions;
using GymTracker.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace GymTracker.Web.Tests;

public class TargetHistoryModelTests
{
    [Fact]
    public void The_snapshot_matches_the_runtime_model_and_the_migration_only_adds_the_history_table()
    {
        using var identity = new ServiceCollection()
            .Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .BuildServiceProvider();
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .UseApplicationServiceProvider(identity).Options, new FixedCurrentUser(null));

        db.Database.HasPendingModelChanges().Should().BeFalse();
        var sql = db.GetService<IMigrator>().GenerateScript("UsualFoods", "DailyNutritionTargets");
        sql.Should().Contain("CREATE TABLE \"NutritionTargetDays\"")
            .And.Contain("ON DELETE CASCADE").And.NotContain("DROP TABLE");
    }
}
