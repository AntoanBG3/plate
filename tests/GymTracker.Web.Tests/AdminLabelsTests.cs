using System.Net;
using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Web.Cli;
using GymTracker.Web.Features.Admin;
using GymTracker.Web.Features.Feedback;
using GymTracker.Web.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GymTracker.Web.Tests;

/// <summary>Label scans in the admin area and the feedback CLI, and the label model's accuracy (SPEC 11, 12).</summary>
[Collection(PostgresCollection.Name)]
public class AdminLabelsTests(PostgresFixture fixture)
{
    private static readonly byte[] Photo = [0xFF, 0xD8, 0xFF, 0x42];

    /// <summary>A scan by a new account under a model name only this test uses, so the shared database's other scans don't interfere.</summary>
    private async Task<LabelScan> ScanAsync(string model, bool edited = false, string? error = null, bool photo = true, int daysAgo = 0)
    {
        var userId = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext(userId);
        var scan = new LabelScan
        {
            UserId = userId,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo),
            Model = model,
            LatencyMs = 900,
            Error = error,
            Reading = """{"energy_kcal":62}""",
            Values = error is null ? """{"kcal":62}""" : null,
            Saved = error is null ? """{"kcalPer100g":64}""" : null,
            Edited = edited,
            FoodItemId = null,
            Photo = photo ? new LabelScanPhoto { ContentType = "image/jpeg", Width = 10, Height = 10, Bytes = Photo } : null,
        };
        db.LabelScans.Add(scan);
        await db.SaveChangesAsync();
        return scan;
    }

    private static string Unique(string prefix) => $"{prefix}/{Guid.NewGuid():N}"[..(prefix.Length + 9)];

    [Fact]
    public async Task The_admin_lists_scans_from_every_account_and_filters_edited_ones()
    {
        await using var app = new AppFactory(fixture);
        var admin = await AdminTests.UnlockedDevAsync(app);
        var model = Unique("list");
        var plain = await ScanAsync(model);
        var edited = await ScanAsync(model, edited: true);

        var all = WebUtility.HtmlDecode(await admin.GetStringAsync("/admin/labels?since=7d"));
        all.Should().Contain($"/admin/labels/{plain.Id}").And.Contain($"/admin/labels/{edited.Id}");
        all.IndexOf($"/admin/labels/{edited.Id}", StringComparison.Ordinal).Should().BeLessThan(all.IndexOf($"/admin/labels/{plain.Id}", StringComparison.Ordinal), "newest first");

        var only = WebUtility.HtmlDecode(await admin.GetStringAsync("/admin/labels?since=7d&edited=true"));
        only.Should().Contain($"/admin/labels/{edited.Id}").And.NotContain($"/admin/labels/{plain.Id}\"");
    }

    [Fact]
    public async Task A_scan_page_shows_the_photo_and_what_was_read_and_saved()
    {
        await using var app = new AppFactory(fixture);
        var admin = await AdminTests.UnlockedDevAsync(app);
        var scan = await ScanAsync(Unique("show"));

        var page = WebUtility.HtmlDecode(await admin.GetStringAsync($"/admin/labels/{scan.Id}"));
        page.Should().Contain($"src=\"/admin/labels/{scan.Id}/photo\"").And.Contain("energy_kcal").And.Contain("kcalPer100g");

        var photo = await admin.GetAsync($"/admin/labels/{scan.Id}/photo");
        photo.StatusCode.Should().Be(HttpStatusCode.OK);
        photo.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        photo.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await photo.Content.ReadAsByteArrayAsync()).Should().Equal(Photo);
        await using var db = fixture.CreateContext(null);
        (await db.AdminAudit.AnyAsync(a => a.Action == AuditActions.LabelPhotoDownload && a.Target == $"label scan #{scan.Id}")).Should().BeTrue();
    }

    [Fact]
    public async Task After_the_photo_is_deleted_the_page_says_so()
    {
        await using var app = new AppFactory(fixture);
        var admin = await AdminTests.UnlockedDevAsync(app);
        var scan = await ScanAsync(Unique("gone"), photo: false);

        WebUtility.HtmlDecode(await admin.GetStringAsync($"/admin/labels/{scan.Id}")).Should().Contain("Photo deleted after 30 days.");
        (await admin.GetAsync($"/admin/labels/{scan.Id}/photo")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Other_accounts_get_404_on_every_label_admin_route()
    {
        await using var app = new AppFactory(fixture);
        var (client, _) = await app.SignedInClientAsync();
        var scan = await ScanAsync(Unique("hidden"));

        foreach (var path in new[] { "/admin/labels", $"/admin/labels/{scan.Id}", $"/admin/labels/{scan.Id}/photo" })
        {
            (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.NotFound, path);
        }
    }

    [Fact]
    public async Task Label_accuracy_counts_failures_and_edits_per_model()
    {
        var model = Unique("acc");
        for (var i = 0; i < 20; i++)
        {
            await ScanAsync(model, edited: i < 5, error: i is 18 or 19 ? "Couldn't read the label. Enter the values by hand." : null);
        }

        await ScanAsync(model, edited: true, daysAgo: 100); // outside the 90 days
        await using var app = new AppFactory(fixture);
        using var scope = app.Services.CreateScope();
        var stats = (await scope.ServiceProvider.GetRequiredService<ModelAccuracyReport>().ForAsync(AiTask.Label, default))[model];

        (stats.Runs, stats.Confirmed, stats.Enough).Should().Be((20, 18, true));
        stats.CorrectionRate.Should().Be(Math.Round(5m / 18, 3));
        stats.DownRate.Should().Be(0.1m, "two of twenty reads failed");
    }

    [Fact]
    public async Task The_cli_lists_shows_and_exports_label_scans_and_the_digest_counts_them()
    {
        await using var app = new AppFactory(fixture);
        var model = Unique("cli");
        var plain = await ScanAsync(model);
        var edited = await ScanAsync(model, edited: true);

        var list = new StringWriter();
        (await FeedbackCommand.RunAsync(app.Services, ["feedback", "labels", "--since", "1d", "--edited"], list)).Should().Be(0);
        list.ToString().Should().Contain($"#{edited.Id}").And.NotContain($"#{plain.Id} ");

        var show = new StringWriter();
        (await FeedbackCommand.RunAsync(app.Services, ["feedback", "label", edited.Id.ToString()], show)).Should().Be(0);
        show.ToString().Should().Contain(model).And.Contain("energy_kcal").And.Contain("kcalPer100g");

        using var bytes = new MemoryStream();
        (await FeedbackCommand.RunAsync(app.Services, ["feedback", "label-photo", edited.Id.ToString()], new StringWriter(), bytes)).Should().Be(0);
        bytes.ToArray().Should().Equal(Photo);
        await using (var db = fixture.CreateContext(null))
        {
            (await db.AdminAudit.AnyAsync(a => a.Action == AuditActions.LabelPhotoDownload && a.Target == $"label scan #{edited.Id}")).Should().BeTrue();
        }

        var digest = new StringWriter();
        (await FeedbackCommand.RunAsync(app.Services, ["feedback", "digest", "--since", "1d"], digest)).Should().Be(0);
        digest.ToString().Should().MatchRegex(@"Label reads: \d+ \(\d+ failed, \d+ edited\)");
    }

    [Fact]
    public async Task The_label_model_picker_shows_reads_edits_and_failures()
    {
        const string model = "google/gemini-3.5-flash-lite";
        for (var i = 0; i < 20; i++)
        {
            await ScanAsync(model, edited: i < 5, error: i is 18 or 19 ? "Couldn't read the label. Enter the values by hand." : null);
        }

        // OpenRouter's model list comes from the stub, so the picker lists the vision model the scans used.
        await using var app = new AppFactory(fixture, services: s => s.AddHttpClient<OpenRouterModels>().ConfigurePrimaryHttpMessageHandler(() => new StubOpenRouter()));
        var admin = await AdminTests.UnlockedDevAsync(app);

        var page = WebUtility.HtmlDecode(await admin.GetStringAsync("/admin/ai/model/label"));

        page.Should().Contain("Last 90 days: 20 reads, 28% of saved ones edited, 10% failed");
    }
}
