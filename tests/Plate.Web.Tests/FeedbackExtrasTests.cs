using FluentAssertions;
using Plate.Core.Domain;
using Plate.Core.Nutrition;
using Plate.Web.Cli;
using Plate.Web.Features.Feedback;
using Plate.Web.Features.Undo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plate.Web.Tests;

/// <summary>What's new, model accuracy, export and retention for feedback (SPEC 11).</summary>
public class FeedbackExtrasTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Whats_new_shows_once_in_the_users_language_and_marks_their_own_report()
    {
        using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();
        var token = await AppFactory.TokenAsync(client, "/feedback/new");
        var feedbackId = int.Parse(System.Text.Json.Nodes.JsonNode.Parse(await (await QueuePost(client, token)).Content.ReadAsStringAsync())!["id"]!.ToString());
        var tag = Guid.NewGuid().ToString("N")[..6];

        (await FeedbackCommand.RunAsync(app.Services, ["feedback", "changelog", "add", "--en", $"Plates fixed {tag}", "--bg", $"Дисковете са оправени {tag}", "--feedback", feedbackId.ToString()], new StringWriter())).Should().Be(0);

        var today = await client.GetStringAsync("/today");
        today.Should().Contain($"Plates fixed {tag}").And.Contain("You reported this");

        var (other, _) = await app.SignedInClientAsync();
        var otherToday = await other.GetStringAsync("/today");
        otherToday.Should().Contain($"Plates fixed {tag}").And.NotContain("You reported this");

        var dismiss = await AppFactory.PostAsync(client, "/today/whats-new/dismiss", await AppFactory.TokenAsync(client, "/today"));
        dismiss.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        (await client.GetStringAsync("/today")).Should().NotContain($"Plates fixed {tag}");

        var otherToken = await AppFactory.TokenAsync(other, "/settings");
        await AppFactory.PostAsync(other, "/settings/appearance", otherToken, new() { ["theme"] = "Dark", ["language"] = "Bulgarian" });

        // Razor writes Cyrillic as character references.
        (await other.GetStringAsync("/today")).Should().Contain(System.Text.Encodings.Web.HtmlEncoder.Default.Encode($"Дисковете са оправени {tag}"));
    }

    [Fact]
    public async Task The_model_page_shows_correction_and_thumbs_down_rates()
    {
        using var app = new AppFactory(fixture);
        var userId = await fixture.CreateUserAsync();
        var model = "test/model-" + Guid.NewGuid().ToString("N")[..6];
        await using (var db = fixture.CreateContext(userId))
        {
            for (var n = 0; n < 20; n++)
            {
                var run = new ParseRun
                {
                    UserId = userId, CreatedAt = DateTimeOffset.UtcNow, Text = "x", AiModel = model,
                    ConfirmedAt = DateTimeOffset.UtcNow, Corrections = n < 5 ? ParseCorrection.GramsChanged : ParseCorrection.None,
                };
                db.ParseRuns.Add(run);
                await db.SaveChangesAsync();
                if (n == 0)
                {
                    db.Feedback.Add(new Core.Domain.Feedback { ClientId = Guid.NewGuid(), UserId = userId, CreatedAt = DateTimeOffset.UtcNow, Source = FeedbackSource.ParseRating, Rating = FeedbackRating.Down, ParseRunId = run.Id });
                    await db.SaveChangesAsync();
                }
            }
        }

        using var scope = app.Services.CreateScope();
        var stats = await scope.ServiceProvider.GetRequiredService<ModelAccuracyReport>().ForAsync(Plate.Web.Integrations.AiTask.Parser, default);

        stats[model].Should().Be(new ModelStats(model, 20, 20, 0.25m, 0.05m, true));
    }

    [Fact]
    public async Task Old_unreported_parse_runs_and_quiet_errors_are_purged_and_entries_keep_their_numbers()
    {
        var userId = await fixture.CreateUserAsync();
        var old = DateTimeOffset.UtcNow.AddDays(-200);
        await using var db = fixture.CreateContext(userId);
        var unreported = new ParseRun { UserId = userId, CreatedAt = old, Text = "old" };
        var reported = new ParseRun { UserId = userId, CreatedAt = old, Text = "old but reported" };
        var recent = new ParseRun { UserId = userId, CreatedAt = DateTimeOffset.UtcNow, Text = "recent" };
        db.ParseRuns.AddRange(unreported, reported, recent);
        await db.SaveChangesAsync();
        db.Feedback.Add(new Core.Domain.Feedback { ClientId = Guid.NewGuid(), UserId = userId, CreatedAt = old, Message = "m", ParseRunId = reported.Id });
        db.FoodLog.Add(new FoodLogEntry { UserId = userId, Date = new DateOnly(2026, 3, 1), Description = "kept", Kcal = 100, ParseRunId = unreported.Id, CreatedAt = old });
        var quiet = new ErrorEvent { Fingerprint = Guid.NewGuid().ToString("N")[..16], Type = "Quiet", FirstSeenAt = old, LastSeenAt = old, Count = 1 };
        var active = new ErrorEvent { Fingerprint = Guid.NewGuid().ToString("N")[..16], Type = "Active", FirstSeenAt = old, LastSeenAt = DateTimeOffset.UtcNow, Count = 3 };
        db.ErrorEvents.AddRange(quiet, active);
        await db.SaveChangesAsync();

        await SoftDeletePurge.PurgeFeedbackAsync(db, DateTimeOffset.UtcNow, default);

        db.ChangeTracker.Clear();
        (await db.ParseRuns.Where(r => r.UserId == userId).Select(r => r.Text).ToListAsync()).Should().BeEquivalentTo("old but reported", "recent");
        var entry = await db.FoodLog.SingleAsync(e => e.Description == "kept");
        (entry.ParseRunId, entry.Kcal).Should().Be(((int?)null, 100m));
        (await db.ErrorEvents.AnyAsync(e => e.Id == quiet.Id)).Should().BeFalse();
        (await db.ErrorEvents.AnyAsync(e => e.Id == active.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task The_export_has_the_users_own_reports()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        s.Db.Feedback.Add(new Core.Domain.Feedback
        {
            ClientId = Guid.NewGuid(), UserId = s.UserId, CreatedAt = new DateTimeOffset(2026, 10, 4, 8, 0, 0, TimeSpan.Zero), Kind = FeedbackKind.Idea,
            Area = FeedbackArea.Templates, Message = "Folders for templates", Status = FeedbackStatus.Planned, Reply = "Next week",
        });
        await s.Db.SaveChangesAsync();

        var files = ExportTests.Unzip(await new Plate.Web.Features.Export.DataExport(s.Db).ZipAsync(default));

        files["feedback.csv"].Should().StartWith("created_at_utc,source,kind,area,rating,reasons,message,status,reply\r\n")
            .And.Contain("Settings,Idea,Templates,,,Folders for templates,Planned,Next week");
    }

    private static async Task<HttpResponseMessage> QueuePost(HttpClient client, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/feedback")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["clientId"] = Guid.NewGuid().ToString(), ["kind"] = "Problem", ["area"] = "PlateCalculator", ["source"] = "Settings", ["message"] = "Plates are wrong",
            }),
        };
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("HX-Request", "true");
        request.Headers.Add("X-Gym-Queue", "1");
        request.Headers.Add("X-Gym-User", await AppFactory.QueueOwnerAsync(client));
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Label_photos_go_after_30_days_and_scans_after_180()
    {
        var userId = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext(userId);
        LabelScan Scan(int daysAgo) => new()
        {
            UserId = userId, CreatedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo), Model = $"purge/{daysAgo}",
            Photos = [new LabelScanPhoto { ContentType = "image/jpeg", Bytes = [0xFF, 0xD8, 0xFF] }],
        };
        var fresh = Scan(29);
        var month = Scan(31);
        var old = Scan(181);
        db.LabelScans.AddRange(fresh, month, old);
        await db.SaveChangesAsync();

        await SoftDeletePurge.PurgeFeedbackAsync(db, DateTimeOffset.UtcNow, default);

        db.ChangeTracker.Clear();
        var left = await db.LabelScans.Include(s => s.Photos).Where(s => s.UserId == userId).ToDictionaryAsync(s => s.Id);
        left.Keys.Should().BeEquivalentTo([fresh.Id, month.Id]);
        left[fresh.Id].Photos.Should().NotBeEmpty();
        left[month.Id].Photos.Should().BeEmpty("the photo is kept 30 days, the scan 180");
    }
}
