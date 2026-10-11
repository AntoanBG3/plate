using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Web.Data;
using GymTracker.Web.Features.Body;
using GymTracker.Web.Features.Food;
using GymTracker.Web.Features.Profile;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.Web.Tests;

public class AdaptiveTargetsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    // Noon UTC is mid-afternoon in Sofia, so the user's day is the same date.
    private static readonly DateOnly Today = new(2026, 9, 1);

    private sealed class Clock(DateOnly day) : TimeProvider
    {
        public DateOnly Day { get; set; } = day;

        public override DateTimeOffset GetUtcNow() => new(Day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
    }

    private sealed record Setup(UserScope Scope, Clock Clock, NutritionTargets Targets, ProfileService Profiles) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Scope.DisposeAsync();
    }

    private async Task<Setup> SetupAsync(int? statedMaintenance = 2500, Goal goal = Goal.Maintain, decimal rate = 0)
    {
        var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var clock = new Clock(Today);
        var profiles = new ProfileService(s.Db, clock);
        await profiles.UpdateAsync(p =>
        {
            p.InitialMaintenanceKcal = statedMaintenance;
            p.Sex = Sex.Male;
            p.Goal = goal;
            p.TargetRatePercentPerWeek = rate;
        }, default);
        return new Setup(s, clock, new NutritionTargets(s.Db, new FixedCurrentUser(s.UserId), profiles, s.Bodyweights, clock), profiles);
    }

    /// <summary>Weigh-ins and food for each of the given days before today.</summary>
    private static async Task LogDaysAsync(UserScope s, int days, decimal kg, decimal kcal)
    {
        for (var d = days; d >= 1; d--)
        {
            var date = Today.AddDays(-d);
            s.Db.Bodyweights.Add(new BodyweightEntry { UserId = s.UserId, Date = date, WeightKg = kg });
            s.Db.FoodLog.Add(new FoodLogEntry
            {
                UserId = s.UserId, Date = date, Meal = Meal.Lunch, Description = "Food", Grams = 100,
                Kcal = kcal, Source = EntrySource.Manual, CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await s.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task Without_logs_the_target_is_the_prior_marked_initial()
    {
        await using var x = await SetupAsync(statedMaintenance: 2500);

        var targets = await x.Targets.ForTodayAsync(default);

        targets.Kcal.Should().Be(2500);
        targets.Estimate!.Method.Should().Be(MaintenanceMethod.Initial);
        targets.ProteinG.Should().BeNull("there is no weigh-in yet");
        targets.Missing.Should().Be("Log your weight to get a protein target.");
    }

    [Fact]
    public async Task Three_weeks_of_logs_give_an_adaptive_estimate_that_is_stored_and_reused_for_a_week()
    {
        await using var x = await SetupAsync(statedMaintenance: 2500);
        await LogDaysAsync(x.Scope, 21, 80m, 2300m);

        var first = await x.Targets.ForTodayAsync(default);

        // Stable weight at 2300 kcal: 0.7 * 2500 + 0.3 * 2300 = 2440.
        first.Estimate!.Method.Should().Be(MaintenanceMethod.Adaptive);
        first.Estimate.EstimatedTdeeKcal.Should().Be(2440);
        first.Estimate.DaysOfData.Should().Be(21);
        first.Kcal.Should().Be(2440);
        first.ProteinG.Should().Be(145, "Auto protein on Maintain is 1.8 g/kg of 80 kg");
        first.Macros!.ProteinMinG.Should().Be(130);
        first.TrendWeightKg.Should().Be(80m);

        x.Clock.Day = Today.AddDays(6);
        (await x.Targets.ForTodayAsync(default)).Estimate!.Id.Should().Be(first.Estimate.Id, "a second call within 7 days reuses it");

        x.Clock.Day = Today.AddDays(7);
        var next = await x.Targets.ForTodayAsync(default);
        next.Estimate!.Id.Should().NotBe(first.Estimate.Id);
        next.Estimate.Method.Should().Be(MaintenanceMethod.Initial, "nothing was logged in the last week");
        next.Estimate.EstimatedTdeeKcal.Should().Be(2440, "the last adaptive estimate is carried forward");

        (await x.Scope.Db.MaintenanceEstimates.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_cut_below_the_floor_is_raised_to_it()
    {
        await using var x = await SetupAsync(statedMaintenance: 2000, goal: Goal.Cut, rate: 1.0m);
        await x.Scope.Bodyweights.SetAsync(Today, 100m, default);

        var targets = await x.Targets.ForTodayAsync(default);

        targets.Kcal.Should().Be(1800);
        targets.FloorApplied.Should().BeTrue();
        targets.FloorKcal.Should().Be(1800);
    }

    [Fact]
    public async Task Profile_edits_apply_at_once_and_update_the_stored_targets()
    {
        await using var x = await SetupAsync(statedMaintenance: 2500);
        await x.Scope.Bodyweights.SetAsync(Today, 80m, default);
        await x.Targets.ForTodayAsync(default);

        await x.Profiles.UpdateAsync(p => p.InitialMaintenanceKcal = 2700, default);
        (await x.Targets.ForTodayAsync(default)).Kcal.Should().Be(2700, "before any adaptive estimate, the prior follows the profile");

        await x.Profiles.UpdateAsync(p => { p.Goal = Goal.Bulk; p.TargetRatePercentPerWeek = 0.25m; }, default);
        var bulk = await x.Targets.ForTodayAsync(default);
        bulk.Kcal.Should().Be(2920); // 80 kg * 0.25% = 0.2 kg/week = 220 kcal/day
        (await x.Scope.Db.MaintenanceEstimates.SingleAsync()).TargetKcal.Should().Be(2920);
    }

    [Fact]
    public async Task The_estimated_prior_follows_the_activity_level()
    {
        await using var x = await SetupAsync(statedMaintenance: null);
        await x.Profiles.UpdateAsync(p => { p.HeightCm = 180; p.BirthYear = 1996; }, default);
        await x.Scope.Bodyweights.SetAsync(Today, 80m, default);

        (await x.Targets.ForTodayAsync(default)).Kcal.Should().Be(2726, "no activity level counts as inactive (NASEM 2023, male, 30 y, 180 cm, 80 kg)");

        await x.Profiles.UpdateAsync(p => p.ActivityLevel = ActivityLevel.LowActive, default);
        var targets = await x.Targets.ForTodayAsync(default);
        targets.Kcal.Should().Be(2946);
        targets.Estimate!.Method.Should().Be(MaintenanceMethod.Initial);
    }

    [Fact]
    public async Task Estimates_are_private_to_each_user()
    {
        await using var a = await SetupAsync();
        await a.Targets.ForTodayAsync(default);

        await using var b = new UserScope(fixture, await fixture.CreateUserAsync());
        (await b.Db.MaintenanceEstimates.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Calorie_chart_shows_each_days_intake_against_the_target_in_force()
    {
        await using var x = await SetupAsync(statedMaintenance: 2500);
        await LogDaysAsync(x.Scope, 3, 80m, 2300m);
        await x.Targets.ForTodayAsync(default);

        var days = await new BodyAnalytics(x.Scope.Db, x.Scope.Bodyweights).CaloriesAsync(Today, default);

        days.Should().HaveCount(28);
        days[^1].Date.Should().Be(Today);
        days[^2].Kcal.Should().Be(2300);
        days[^2].TargetKcal.Should().BeNull("the first estimate is from today");
        days[^1].TargetKcal.Should().Be(2500);
        days[^1].Kcal.Should().BeNull();

        var weight = await new BodyAnalytics(x.Scope.Db, x.Scope.Bodyweights).WeightAsync("4w", Today, default);
        weight.Points.Should().HaveCount(4);
        weight.Points.Should().OnlyContain(p => p.TrendKg == 80m);
        (await new BodyAnalytics(x.Scope.Db, x.Scope.Bodyweights).WeightAsync("bogus", Today, default)).Range.Should().Be("12w");
    }
}
