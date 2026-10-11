using System.IO.Compression;
using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.E2E.Tests;

/// <summary>The areas the 2026-10-05 QA pass couldn't reach by hand, end to end in the browser (Phase 11).</summary>
public class UntestedAreasTests(E2EFixture app) : IClassFixture<E2EFixture>
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

        (await phone.Page.TitleAsync()).Should().Be("Offline · Repa");
    }

    // Chromium only: the virtual authenticator is a Chrome DevTools feature. It stands in for Face ID: resident key,
    // user verification done (the app requires it, SPEC 12).
    [Fact]
    public async Task A_passkey_is_added_signs_in_and_unlocks_the_admin_area()
    {
        await using var phone = await app.SignedInPhoneAsync(E2EFixture.Chromium);
        var cdp = await phone.Page.Context.NewCDPSessionAsync(phone.Page);
        await cdp.SendAsync("WebAuthn.enable");
        await cdp.SendAsync("WebAuthn.addVirtualAuthenticator", new Dictionary<string, object>
        {
            ["options"] = new Dictionary<string, object>
            {
                ["protocol"] = "ctap2", ["transport"] = "internal", ["hasResidentKey"] = true,
                ["hasUserVerification"] = true, ["isUserVerified"] = true, ["automaticPresenceSimulation"] = true,
            },
        });
        (await GymTracker.Web.Cli.RoleCommand.RunAsync(app.App.Services, ["role", "grant", "dev", "--email", phone.Email], new StringWriter())).Should().Be(0);

        // Add one in Settings.
        await phone.GoAsync("/settings/security");
        await phone.NavigateByAsync(() => phone.Page.Locator("[data-module=passkey-add]").TapAsync());
        await using (var db = app.Db.CreateContext(null))
        {
            var userId = await UserIdAsync(phone);
            (await db.UserPasskeys.CountAsync(p => p.UserId == userId)).Should().Be(1);
        }

        // Sign in with it, with no password, from the button. The virtual authenticator would also answer the autofill
        // request the page starts on its own (a person has to pick the passkey there), racing the button; so autofill
        // is reported unavailable here.
        await phone.Page.AddInitScriptAsync("PublicKeyCredential.isConditionalMediationAvailable = () => Promise.resolve(false);");
        await phone.Page.Context.ClearCookiesAsync();
        await phone.GoAsync("/account/login");
        await phone.Page.Locator("[data-module=passkey-login]").TapAsync();
        await phone.Page.WaitForURLAsync(url => !url.Contains("/account/login"));

        // And unlock the admin area.
        await phone.GoAsync("/admin/unlock");
        await phone.Page.Locator("[data-module=passkey-unlock]").TapAsync();
        await phone.Page.WaitForURLAsync(url => new Uri(url).AbsolutePath.StartsWith("/admin") && !url.Contains("/unlock"));
        (await phone.Page.Locator("main").InnerTextAsync()).Should().Contain("Lock now");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_photo_of_a_barcode_is_read_and_looked_up(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=barcode");
        const string code = "4006381333931";

        await phone.NavigateByAsync(() => phone.Page.Locator("#scanner [data-photo]").SetInputFilesAsync(new Microsoft.Playwright.FilePayload
        {
            Name = "barcode.png", MimeType = "image/png", Buffer = Ean13Png(code),
        }));

        new Uri(phone.Page.Url).AbsolutePath.Should().Be("/food/barcode");
        phone.Page.Url.Should().Contain($"code={code}");
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_product_without_a_name_is_named_when_it_is_logged(string browser)
    {
        // Feedback #5: Open Food Facts had no name for a Monster can.
        var digits = Enumerable.Range(0, 12).Select(i => i == 0 ? Random.Shared.Next(1, 10) : Random.Shared.Next(10)).ToArray();
        var code = string.Concat(digits) + (10 - digits.Select((d, i) => i % 2 == 0 ? d : d * 3).Sum() % 10) % 10;
        await using (var db = app.Db.CreateContext(null))
        {
            db.FoodItems.Add(new FoodItem { Name = "Monster 500ml", Brand = "Monster", Barcode = code, KcalPer100g = 2, CarbsPer100g = 0.9m, Source = FoodSource.OpenFoodFacts, Unnamed = true });
            await db.SaveChangesAsync();
        }

        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync($"/food/barcode?date={Today}&meal=Lunch&code={code}");
        var name = phone.Page.Locator("#b-name");
        (await name.InputValueAsync()).Should().Be("Monster 500ml");
        var box = await Phone.BoxAsync(name);
        (box.X + box.Width).Should().BeLessThanOrEqualTo(390);
        box.Height.Should().BeGreaterThanOrEqualTo(44);

        await name.FillAsync("Monster Ultra White");
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/log-item'] button[type=submit]").First.TapAsync());

        (await phone.Page.Locator("#meal-lunch").InnerTextAsync()).Should().Contain("Monster Ultra White").And.NotContain("(Monster)");
        phone.ShouldHaveNoErrors();
    }

    // An EAN-13 drawn as black bars on white: 4 px per module, quiet zones of 12 modules (GS1 asks for 11 and 7).
    internal static byte[] Ean13Png(string code)
    {
        string[] l = ["0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011"];
        string[] parity = ["LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL"];
        static string Invert(string bits) => new(bits.Select(b => b == '0' ? '1' : '0').ToArray());
        var digits = code.Select(c => c - '0').ToArray();
        var bars = new System.Text.StringBuilder("101");
        for (var i = 1; i <= 6; i++)
        {
            bars.Append(parity[digits[0]][i - 1] == 'L' ? l[digits[i]] : new string(Invert(l[digits[i]]).Reverse().ToArray()));
        }

        bars.Append("01010");
        for (var i = 7; i <= 12; i++)
        {
            bars.Append(Invert(l[digits[i]]));
        }

        bars.Append("101");
        const int module = 4, quiet = 12;
        var modules = bars.ToString();
        return Png((modules.Length + 2 * quiet) * module, 160, (x, _) =>
        {
            var m = x / module - quiet;
            return m >= 0 && m < modules.Length && modules[m] == '1';
        });
    }

    // A plain grey PNG of the given size.
    internal static byte[] Png(int width, int height) => Png(width, height, (_, _) => false, light: 128);

    // A PNG made without an image library: RGB rows, each pixel black where dark(x, y), else light grey to white.
    internal static byte[] Png(int width, int height, Func<int, int, bool> dark, byte light = 255)
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
            for (var x = 0; x < width; x++)
            {
                var v = dark(x, y) ? (byte)0 : light;
                raw.WriteByte(v);
                raw.WriteByte(v);
                raw.WriteByte(v);
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
