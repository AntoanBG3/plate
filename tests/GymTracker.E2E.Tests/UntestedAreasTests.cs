using System.IO.Compression;
using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.E2E.Tests;

/// <summary>The areas the 2026-10-05 QA pass couldn't reach by hand, end to end in the browser (Phase 11).</summary>
[Collection(E2ECollection.Name)]
public class UntestedAreasTests(E2EFixture app)
{
    private static string Today => UserClock.Today(DateTimeOffset.UtcNow, UserProfile.DefaultTimeZoneId).ToString("yyyy-MM-dd");

    private async Task<string> UserIdAsync(Phone phone)
    {
        await using var db = app.Db.CreateContext(null);
        return await db.Users.Where(u => u.Email == phone.Email).Select(u => u.Id).SingleAsync();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_meal_is_saved_logged_again_and_deleted(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/food");
        await phone.PostAsync("/food/quick", new() { ["date"] = Today, ["meal"] = "Lunch", ["description"] = "Wrap", ["kcal"] = "600", ["protein"] = "40" });
        await phone.GoAsync("/food");

        // Save lunch as a meal from the meal's menu.
        await phone.Page.Locator("#meal-lunch [data-menu-trigger]").TapAsync();
        await phone.Page.Locator("#meal-lunch [role=menuitem]", new() { HasText = "Save as a meal" }).TapAsync();
        await phone.Page.FillAsync("#meal-name", "Usual lunch");
        await phone.NavigateByAsync(() => phone.Page.Locator("#food-sheet-body button[type=submit]").TapAsync());
        (await phone.Page.Locator(".toast, .banner").AllInnerTextsAsync()).Should().Contain(t => t.Contains("Saved as a meal."));

        // Log it to dinner from Recent.
        await phone.GoAsync($"/food/add?date={Today}&meal=Dinner&tab=recent");
        await phone.Page.Locator("button.row-link", new() { HasText = "Usual lunch" }).TapAsync();
        await phone.Page.Locator("#food-sheet-body form[action$='/log'] button[type=submit]").WaitForAsync();
        await phone.NavigateByAsync(() => phone.Page.Locator("#food-sheet-body form[action$='/log'] button[type=submit]").TapAsync());
        (await phone.Page.Locator("#meal-dinner").InnerTextAsync()).Should().Contain("Wrap");

        // Delete it, through the confirmation sheet.
        await phone.GoAsync($"/food/add?date={Today}&meal=Dinner&tab=recent");
        await phone.Page.Locator("button.row-link", new() { HasText = "Usual lunch" }).TapAsync();
        await phone.Page.Locator("#food-sheet-body form[action$='/delete'] button[type=submit]").TapAsync();
        await phone.NavigateByAsync(() => phone.Page.Locator("#confirm-sheet [data-confirm-yes]").TapAsync());
        (await phone.Page.Locator("button.row-link", new() { HasText = "Usual lunch" }).CountAsync()).Should().Be(0);

        await using var db = app.Db.CreateContext(await UserIdAsync(phone));
        (await db.SavedMeals.CountAsync()).Should().Be(0);
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task The_data_export_downloads_a_zip_of_csv_files(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.WithTargetsAsync(app);
        await phone.GoAsync("/settings");

        var download = await phone.Page.RunAndWaitForDownloadAsync(() => phone.Page.Locator("a[href='/settings/export']").TapAsync());
        var path = Path.Combine(Path.GetTempPath(), $"gym-export-{Guid.NewGuid():N}.zip");
        await download.SaveAsAsync(path);
        try
        {
            using var zip = ZipFile.OpenRead(path);
            zip.Entries.Select(e => e.FullName).Should().Contain(["profile.csv", "bodyweight.csv", "workouts.csv", "sets.csv", "food_log.csv"]);
            using var reader = new StreamReader(zip.GetEntry("bodyweight.csv")!.Open());
            (await reader.ReadToEndAsync()).Should().Contain("80");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Feedback_with_a_screenshot_is_sent_and_listed(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/feedback/new");
        await phone.Page.FillAsync("#message", "The rest timer covers the Undo button.");
        await phone.Page.Locator("input[name=screenshot]").SetInputFilesAsync(new Microsoft.Playwright.FilePayload
        {
            Name = "shot.png", MimeType = "image/png", Buffer = Png(40, 80),
        });
        await phone.NavigateByAsync(() => phone.Page.Locator("button[type=submit]", new() { HasText = "Send" }).TapAsync());

        new Uri(phone.Page.Url).AbsolutePath.Should().Be("/feedback");
        (await phone.Page.Locator("main").InnerTextAsync()).Should().Contain("The rest timer covers the Undo button.");
        await using var db = app.Db.CreateContext(null);
        var userId = await UserIdAsync(phone);
        var report = await db.Feedback.IgnoreQueryFilters().SingleAsync(f => f.UserId == userId);
        (await db.FeedbackAttachments.IgnoreQueryFilters().CountAsync(a => a.FeedbackId == report.Id)).Should().Be(1);
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_set_logged_offline_is_kept_and_sent_once_when_back_online(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press");
        var session = await phone.StartSessionAsync(template);

        await phone.Page.Context.SetOfflineAsync(true);
        await phone.LogSetAsync(phone.Page.Locator(".rows.working .set-row").First, "60", "10");
        var status = phone.Page.Locator("#queue-status");
        await status.WaitForAsync();
        (await status.InnerTextAsync()).Should().Contain("1 set is saved on this phone");

        // Back online: the queue sends by itself, once.
        await phone.Page.Context.SetOfflineAsync(false);
        await phone.Page.EvaluateAsync("() => window.dispatchEvent(new Event('online'))");
        await status.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Hidden });
        await phone.GoAsync($"/train/sessions/{session}");
        (await phone.Page.Locator(".set-row.done").CountAsync()).Should().Be(1);

        await using var db = app.Db.CreateContext(await UserIdAsync(phone));
        var sets = await db.Sets.Where(x => x.SessionExercise.SessionId == session).ToListAsync();
        sets.Should().ContainSingle().Which.WeightKg.Should().Be(60);
    }

    // Chromium only: Playwright's WebKit fails an offline load before the service worker can answer it. Safari on the
    // iPhone does answer it; that is on the iOS checklist.
    [Theory]
    [InlineData(E2EFixture.Chromium)]
    public async Task Offline_a_page_that_cannot_load_shows_the_offline_page(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/today");
        await phone.Page.EvaluateAsync("() => navigator.serviceWorker.ready.then(() => true)");

        await phone.Page.Context.SetOfflineAsync(true);
        try
        {
            await phone.Page.GotoAsync("/progress");
        }
        finally
        {
            await phone.Page.Context.SetOfflineAsync(false);
        }

        (await phone.Page.TitleAsync()).Should().Be("Offline · Gym");
    }

    // A plain PNG of the given size, made without an image library: one grey RGB pixel row repeated.
    internal static byte[] Png(int width, int height)
    {
        static byte[] Chunk(string type, byte[] data)
        {
            var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            var length = BitConverter.GetBytes(data.Length).Reverse();
            var crc = BitConverter.GetBytes(Crc32(body)).Reverse();
            return length.Concat(body).Concat(crc).ToArray();
        }

        static uint Crc32(byte[] bytes)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in bytes)
            {
                crc ^= b;
                for (var i = 0; i < 8; i++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                }
            }

            return ~crc;
        }

        var header = new byte[13];
        BitConverter.GetBytes(width).Reverse().ToArray().CopyTo(header, 0);
        BitConverter.GetBytes(height).Reverse().ToArray().CopyTo(header, 4);
        header[8] = 8; // bit depth
        header[9] = 2; // RGB
        var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < width * 3; x++)
            {
                raw.WriteByte(128);
            }
        }

        var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(z);
        }

        return new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }
            .Concat(Chunk("IHDR", header)).Concat(Chunk("IDAT", compressed.ToArray())).Concat(Chunk("IEND", [])).ToArray();
    }
}
