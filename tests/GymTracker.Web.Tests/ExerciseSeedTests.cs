using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Web.Data;
using GymTracker.Web.Data.Seed;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

/// <summary>Exercises added to the seed reach a database that already has the library, run on a separate database.</summary>
public class ExerciseSeedTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Seeding_an_existing_library_adds_only_the_missing_exercises()
    {
        var connection = await fixture.NewDatabaseAsync("exercise_seed");
        await using (var db = fixture.CreateContext(null, connection))
        {
            await db.Database.MigrateAsync();
            // An older library: the first ten exercises, one of them archived, plus a user's custom exercise that
            // shares a name with a library exercise.
            db.Exercises.AddRange(ExerciseSeed.All().Take(10));
            await db.SaveChangesAsync();
            await db.Exercises.IgnoreQueryFilters().Where(e => e.Name == "Dip").ExecuteUpdateAsync(s => s.SetProperty(e => e.IsArchived, true));
            db.Users.Add(new AppUser { Id = "u", UserName = "u@test", Email = "u@test", NormalizedUserName = "U@TEST", NormalizedEmail = "U@TEST" });
            db.Exercises.Add(new Exercise { Name = "Pec deck", Equipment = Equipment.Machine, PrimaryMuscle = Muscle.Chest, OwnerUserId = "u" });
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext(null, connection))
        {
            await ExerciseSeed.SeedAsync(db, CancellationToken.None);
            await ExerciseSeed.SeedAsync(db, CancellationToken.None);

            var shared = await db.Exercises.IgnoreQueryFilters().Where(e => e.OwnerUserId == null).ToListAsync();
            shared.Select(e => e.Name).Should().BeEquivalentTo(ExerciseSeed.All().Select(e => e.Name), "each library exercise is there exactly once");
            shared.Single(e => e.Name == "Dip").IsArchived.Should().BeTrue();
            (await db.Exercises.IgnoreQueryFilters().CountAsync(e => e.OwnerUserId == "u")).Should().Be(1);
        }
    }
}
