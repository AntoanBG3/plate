using System.Net;
using FluentAssertions;
using Plate.Core.Domain;
using Plate.Core.Nutrition;
using Plate.Web.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plate.Web.Tests;

/// <summary>Sharing a barcode food or keeping it private, and the anonymous "Added by another user" mark (SPEC 4, 7).</summary>
public class SharedFoodTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Mark = "Added by another user";
    private static readonly DateOnly Day = new(2026, 10, 5);

    /// <summary>A random valid EAN-13, so tests never collide on the barcode indexes.</summary>
    private static string NewEan()
    {
        var digits = Enumerable.Range(0, 12).Select(i => i == 0 ? Random.Shared.Next(1, 10) : Random.Shared.Next(10)).ToArray();
        var sum = digits.Select((d, i) => i % 2 == 0 ? d : d * 3).Sum();
        return string.Concat(digits) + (10 - sum % 10) % 10;
    }

    private static string Unique(string name) => $"{name} {Guid.NewGuid():N}"[..(name.Length + 9)];

    private AppFactory NewApp() =>
        new(fixture, services: s => s.AddSingleton<IFoodDatabase, FakeFoodDatabase>());

    private sealed record Account(HttpClient Client, string Email, string UserId);

    private async Task<Account> SignInAsync(AppFactory app, string? displayName = null)
    {
        var (client, email) = await app.SignedInClientAsync();
        await using var db = fixture.CreateContext(null);
        var id = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        if (displayName is not null)
        {
            await db.Profiles.IgnoreQueryFilters().Where(p => p.UserId == id).ExecuteUpdateAsync(p => p.SetProperty(x => x.DisplayName, displayName));
        }

        return new Account(client, email, id);
    }

    private static async Task<HttpResponseMessage> SaveManualAsync(Account a, string name, string? barcode, bool? share, string grams = "150", string date = "2026-10-05",
        params KeyValuePair<string, string>[] extra)
    {
        var token = await AppFactory.TokenAsync(a.Client, "/food/add?date=2026-10-05&meal=Lunch&tab=manual");
        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("date", date),
            new("meal", "Lunch"),
            new("name", name),
            new("barcode", barcode ?? ""),
            new("kcal", "62"),
            new("protein", "3.2"),
            new("carbs", "4.1"),
            new("fat", "3.6"),
            new("grams", grams),
        };
        if (share is { } s)
        {
            // As a browser sends it: the ticked checkbox first, then the hidden "false" after it.
            if (s)
            {
                fields.Add(new("share", "true"));
            }

            fields.Add(new("share", "false"));
        }

        fields.AddRange(extra);
        fields.Add(new("requestId", Guid.NewGuid().ToString()));
        return await a.Client.PostAsync("/food/items", new FormUrlEncodedContent(fields));
    }

    private static async Task<string> PageAsync(Account a, string path) =>
        WebUtility.HtmlDecode(await a.Client.GetStringAsync(path));

    private static string Scan(string code) => $"/food/barcode?date=2026-10-05&meal=Lunch&code={code}";

    [Fact]
    public async Task A_shared_food_is_found_by_another_users_scan_with_the_mark_and_no_name()
    {
        await using var app = NewApp();
        var a = await SignInAsync(app, "Zelda Quixote");
        var b = await SignInAsync(app);
        var code = NewEan();
        var name = Unique("Ayran qsf");

        (await SaveManualAsync(a, name, code, share: true)).StatusCode.Should().Be(HttpStatusCode.Redirect);

        var html = await PageAsync(b, Scan(code));
        html.Should().Contain(name).And.Contain(Mark);
        html.Should().NotContain("Zelda").And.NotContain(a.Email);
    }

    [Fact]
    public async Task The_account_that_added_a_food_sees_no_mark()
    {
        await using var app = NewApp();
        var a = await SignInAsync(app);
        var code = NewEan();
        await SaveManualAsync(a, Unique("Kefir qsf"), code, share: true);

        (await PageAsync(a, Scan(code))).Should().NotContain(Mark);
    }

    [Fact]
    public async Task An_unticked_food_stays_private_and_the_other_user_can_save_their_own()
    {
        await using var app = NewApp();
        var a = await SignInAsync(app);
        var b = await SignInAsync(app);
        var code = NewEan();
        var name = Unique("Boza qsf");

        (await SaveManualAsync(a, name, code, share: false)).StatusCode.Should().Be(HttpStatusCode.Redirect);

        (await PageAsync(b, Scan(code))).Should().NotContain(name).And.Contain("Not found");
        (await SaveManualAsync(b, Unique("Boza mine qsf"), code, share: false)).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await PageAsync(a, Scan(code))).Should().Contain(name);
    }

    [Fact]
    public async Task A_users_own_private_food_comes_before_a_shared_one()
    {
        await using var app = NewApp();
        var a = await SignInAsync(app);
        var b = await SignInAsync(app);
        var code = NewEan();
        var mine = Unique("Mine qsf");
        await SaveManualAsync(b, mine, code, share: false);
        await SaveManualAsync(a, Unique("Theirs qsf"), code, share: true);

        (await PageAsync(b, Scan(code))).Should().Contain(mine).And.NotContain(Mark);
    }

    [Fact]
    public async Task The_search_line_and_the_entry_sheet_show_the_mark()
    {
        await using var app = NewApp();
        var a = await SignInAsync(app);
        var b = await SignInAsync(app);
        var name = Unique("Lyutenitsa qsf");
        await SaveManualAsync(a, name, NewEan(), share: true);

        var search = await PageAsync(b, $"/food/add?date=2026-10-05&meal=Lunch&tab=search&q={Uri.EscapeDataString(name)}");
        search.Should().Contain(name).And.Contain(Mark);

        await using var db = fixture.CreateContext(b.UserId);
        var food = await db.FoodItems.SingleAsync(f => f.Name == name);
        db.FoodLog.Add(new FoodLogEntry { UserId = b.UserId, Date = Day, Meal = Meal.Lunch, Description = name, Grams = 100, FoodItemId = food.Id, Source = EntrySource.Search, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var entryId = await db.FoodLog.Where(e => e.FoodItemId == food.Id).Select(e => e.Id).SingleAsync();

        (await PageAsync(b, $"/food/entries/{entryId}")).Should().Contain(Mark);
    }

    [Fact]
    public async Task Open_Food_Facts_and_USDA_foods_show_no_mark()
    {
        var off = new FakeFoodDatabase();
        var code = NewEan();
        off.Products[code] = new ProductLookup(LookupStatus.Found, new ProductInfo(code, "Cached cola qsf", "Brand", new Per100g(42, 0, 10.6m, 0), 330));
        await using var app = new AppFactory(fixture, services: s => s.AddSingleton<IFoodDatabase>(off));
        var b = await SignInAsync(app);

        (await PageAsync(b, Scan(code))).Should().Contain("Cached cola qsf").And.NotContain(Mark);
        (await PageAsync(b, "/food/add?date=2026-10-05&meal=Lunch&tab=search&q=rice")).Should().NotContain(Mark);
    }

    private static async Task<HttpResponseMessage> LogScannedAsync(Account a, string code, int foodItemId, string? name)
    {
        var token = await AppFactory.TokenAsync(a.Client, Scan(code));
        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("date", "2026-10-05"),
            new("meal", "Lunch"),
            new("foodItemId", foodItemId.ToString()),
            new("grams", "500"),
            new("source", "Barcode"),
        };
        if (name is not null)
        {
            fields.Add(new("name", name));
        }

        fields.Add(new("requestId", Guid.NewGuid().ToString()));
        return await a.Client.PostAsync("/food/log-item", new FormUrlEncodedContent(fields));
    }

    [Fact]
    public async Task The_first_name_given_to_a_product_without_one_is_kept_for_everyone()
    {
        // Feedback #5: Open Food Facts had no name for a Monster can, and it was saved as "Product <barcode>".
        var off = new FakeFoodDatabase();
        var code = NewEan();
        off.Products[code] = new ProductLookup(LookupStatus.Found, new ProductInfo(code, "Monster 500ml", "Monster", new Per100g(2, 0, 0.9m, 0), 500, Unnamed: true));
        await using var app = new AppFactory(fixture, services: s => s.AddSingleton<IFoodDatabase>(off));
        var a = await SignInAsync(app);
        var b = await SignInAsync(app);

        var html = await PageAsync(a, Scan(code));
        html.Should().Contain("name=\"name\"").And.Contain("value=\"Monster 500ml\"");
        int id;
        await using (var db = fixture.CreateContext(null))
        {
            id = await db.FoodItems.Where(f => f.Barcode == code).Select(f => f.Id).SingleAsync();
        }

        var name = Unique("Ultra white qsf");
        (await LogScannedAsync(a, code, id, $"  {name} ")).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await LogScannedAsync(b, code, id, "Someone else's name")).StatusCode.Should().Be(HttpStatusCode.Redirect);

        await using (var db = fixture.CreateContext(null))
        {
            var food = await db.FoodItems.SingleAsync(f => f.Id == id);
            food.Name.Should().Be(name);
            food.Unnamed.Should().BeFalse();
            var entries = await db.FoodLog.IgnoreQueryFilters().Where(e => e.FoodItemId == id).OrderBy(e => e.Id).Select(e => e.Description).ToListAsync();
            entries.Should().Equal($"{name} (Monster)", $"{name} (Monster)");
        }

        (await PageAsync(b, Scan(code))).Should().Contain(name).And.NotContain("name=\"name\"", "a named product is no longer asked about");
    }

    [Fact]
    public async Task A_name_is_not_taken_for_a_product_that_has_one()
    {
        var off = new FakeFoodDatabase();
        var code = NewEan();
        var name = Unique("Named cola qsf");
        off.Products[code] = new ProductLookup(LookupStatus.Found, new ProductInfo(code, name, "Brand", new Per100g(42, 0, 10.6m, 0), 330));
        await using var app = new AppFactory(fixture, services: s => s.AddSingleton<IFoodDatabase>(off));
        var a = await SignInAsync(app);

        (await PageAsync(a, Scan(code))).Should().NotContain("name=\"name\"");
        await using var db = fixture.CreateContext(null);
        var id = await db.FoodItems.Where(f => f.Barcode == code).Select(f => f.Id).SingleAsync();
        (await LogScannedAsync(a, code, id, "Renamed")).StatusCode.Should().Be(HttpStatusCode.Redirect);

        (await db.FoodItems.SingleAsync(f => f.Id == id)).Name.Should().Be(name);
    }

    [Fact]
    public async Task A_food_whose_creator_was_deleted_still_reads_as_another_users()
    {
        await using var app = NewApp();
        var a = await SignInAsync(app);
        var b = await SignInAsync(app);
        var code = NewEan();
        await SaveManualAsync(a, Unique("Orphan qsf"), code, share: true);

        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Plate.Web.Data.AppUser>>();
            await scope.ServiceProvider.GetRequiredService<Plate.Web.Features.Admin.UserDeleter>().DeleteAsync((await users.FindByEmailAsync(a.Email))!, default);
        }

        (await PageAsync(b, Scan(code))).Should().Contain(Mark);
    }

    [Fact]
    public async Task A_food_whose_creator_is_unknown_shows_no_mark()
    {
        // Shared foods from before Phase 13 that nobody logged have no known creator (SPEC 4): nothing is claimed.
        await using var app = NewApp();
        var a = await SignInAsync(app);
        var b = await SignInAsync(app);
        var code = NewEan();
        var name = Unique("Old shared qsf");
        await SaveManualAsync(a, name, code, share: true);
        await using (var db = fixture.CreateContext(null))
        {
            await db.FoodItems.IgnoreQueryFilters().Where(f => f.Barcode == code).ExecuteUpdateAsync(f => f.SetProperty(x => x.AddedByUserId, (string?)null));
        }

        (await PageAsync(b, Scan(code))).Should().NotContain(Mark);
        (await PageAsync(a, Scan(code))).Should().NotContain(Mark);
        (await PageAsync(b, $"/food/add?date=2026-10-05&meal=Lunch&tab=search&q={Uri.EscapeDataString(name)}")).Should().Contain(name).And.NotContain(Mark);
    }

    [Fact]
    public async Task Sharing_a_barcode_someone_already_shared_gives_a_message_not_an_error()
    {
        await using var app = NewApp();
        var a = await SignInAsync(app);
        var b = await SignInAsync(app);
        var code = NewEan();
        await SaveManualAsync(a, Unique("First qsf"), code, share: true);

        // Keep the submitted form and show the error on it.
        var response = await SaveManualAsync(b, Unique("Second qsf"), code, share: true);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("A food with that barcode is already shared. Scan it again to use it.");
    }

    [Fact]
    public async Task A_declined_shared_barcode_is_saved_privately_even_with_sharing_ticked()
    {
        // "No, it's different" sends the code back with the form. Sharing can still arrive ticked (a label read that
        // started before the tap redraws the form with it), and must not be refused as already shared.
        await using var app = NewApp();
        var a = await SignInAsync(app);
        var b = await SignInAsync(app);
        var code = NewEan();
        await SaveManualAsync(a, Unique("Shared fiocchi qsf"), code, share: true);

        var name = Unique("My fiocchi qsf");
        var response = await SaveManualAsync(b, name, code, share: true, extra: [new("declinedBarcode", code)]);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        await using var db = fixture.CreateContext(null);
        var mine = await db.FoodItems.IgnoreQueryFilters().SingleAsync(f => f.Name == name);
        mine.OwnerUserId.Should().Be(b.UserId);
        mine.Barcode.Should().Be(code);
    }

    [Fact]
    public async Task A_form_sent_back_with_an_error_keeps_the_decline_and_hides_sharing()
    {
        await using var app = NewApp();
        var a = await SignInAsync(app);
        var b = await SignInAsync(app);
        var code = NewEan();
        await SaveManualAsync(a, Unique("Shared burrata qsf"), code, share: true);

        var response = await SaveManualAsync(b, Unique("My burrata qsf"), code, share: true, grams: "5000",
            extra: [new("barcodeField", "true"), new("declinedBarcode", code)]);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain($"name=\"declinedBarcode\" value=\"{code}\"")
            .And.Contain("Another food already shares this barcode, so this one is kept for you only.")
            .And.Contain("name=\"share\" value=\"true\" />")
            .And.Contain("data-share style=\"margin-top: var(--s-5)\" hidden=\"hidden\"");
    }

    [Fact]
    public async Task Two_concurrent_shares_of_one_barcode_save_one_food_and_refuse_the_other()
    {
        var (u1, u2) = (await fixture.CreateUserAsync(), await fixture.CreateUserAsync());
        var code = NewEan();
        await using var s1 = new UserScope(fixture, u1);
        await using var s2 = new UserScope(fixture, u2);
        var per100 = new Per100g(62, 3.2m, 4.1m, 3.6m);

        var results = await Task.WhenAll(
            Attempt(() => s1.Foods.CreateManualAsync("Race one qsf", code, per100, null, share: true, default)),
            Attempt(() => s2.Foods.CreateManualAsync("Race two qsf", code, per100, null, share: true, default)));

        results.Count(r => r is null).Should().Be(1, "exactly one share wins");
        results.Single(r => r is not null)!.Text.Should().Be(new Plate.Core.Text.LocalText("A food with that barcode is already shared. Scan it again to use it."));

        static async Task<ValidationException?> Attempt(Func<Task<FoodItem>> create)
        {
            try
            {
                await create();
                return null;
            }
            catch (ValidationException ex)
            {
                return ex;
            }
        }
    }

    [Fact]
    public async Task Without_a_barcode_a_food_is_always_private()
    {
        var (u1, u2) = (await fixture.CreateUserAsync(), await fixture.CreateUserAsync());
        await using var s1 = new UserScope(fixture, u1);
        await using var s2 = new UserScope(fixture, u2);
        var food = await s1.Foods.CreateManualAsync("No code qsf", null, new Per100g(100, 1, 1, 1), null, share: true, default);

        food.OwnerUserId.Should().Be(u1);
        food.AddedByUserId.Should().Be(u1);
        await s2.Invoking(x => x.Foods.GetAsync(food.Id, default)).Should().ThrowAsync<NotFoundException>();
    }

    [Theory]
    [InlineData("5000", "2026-10-05")]
    [InlineData("150", "2099-01-01")]
    public async Task A_refused_entry_saves_no_food_so_a_retry_works(string grams, string date)
    {
        // Too many grams or a future day used to leave the (shared) food saved, and the retry said "already shared".
        await using var app = NewApp();
        var a = await SignInAsync(app);
        var code = NewEan();
        var name = Unique("Retry qsf");

        (await SaveManualAsync(a, name, code, share: true, grams: grams, date: date)).StatusCode.Should().Be(HttpStatusCode.OK);
        await using (var db = fixture.CreateContext(null))
        {
            (await db.FoodItems.IgnoreQueryFilters().AnyAsync(f => f.Barcode == code)).Should().BeFalse("nothing is kept from a refused save");
        }

        var retry = await SaveManualAsync(a, name, code, share: true);
        retry.Headers.Location!.OriginalString.Should().StartWith("/food?date=2026-10-05");
        await using var check = fixture.CreateContext(a.UserId);
        (await check.FoodLog.CountAsync(e => e.Description == name)).Should().Be(1);
    }

    private static string Known(string code) => $"/food/barcode/known?date=2026-10-05&meal=Lunch&code={code}";

    [Fact]
    public async Task A_known_barcode_offers_the_users_own_food()
    {
        await using var app = NewApp();
        var a = await SignInAsync(app);
        var code = NewEan();
        var name = Unique("Kefir mine kbt");
        await SaveManualAsync(a, name, code, share: false);

        var response = await a.Client.GetAsync(Known(code));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        html.Should().Contain(name).And.Contain("Is this the same food?").And.Contain("data-own=\"true\"").And.NotContain(Mark);
        html.Should().Contain($"/food/barcode?date=2026-10-05&meal=Lunch&code={code}");
        html.Should().Contain("You already have a food with this barcode, so this one is saved without it.");
    }

    [Fact]
    public async Task A_known_barcode_shared_by_another_account_carries_the_mark_and_no_name()
    {
        await using var app = NewApp();
        var a = await SignInAsync(app, "Zelda Quixote");
        var b = await SignInAsync(app);
        var code = NewEan();
        var name = Unique("Kefir theirs kbt");
        await SaveManualAsync(a, name, code, share: true);

        var html = WebUtility.HtmlDecode(await (await b.Client.GetAsync(Known(code))).Content.ReadAsStringAsync());

        html.Should().Contain(name).And.Contain(Mark).And.Contain("data-own=\"false\"").And.NotContain("Zelda");
        html.Should().Contain("Another food already shares this barcode, so this one is kept for you only.");
    }

    [Fact]
    public async Task A_known_barcode_you_shared_yourself_says_so_when_rejected()
    {
        await using var app = NewApp();
        var a = await SignInAsync(app);
        var code = NewEan();
        var name = Unique("Kefir shared mine kbt");
        await SaveManualAsync(a, name, code, share: true);

        var html = WebUtility.HtmlDecode(await (await a.Client.GetAsync(Known(code))).Content.ReadAsStringAsync());

        html.Should().Contain(name).And.Contain("data-own=\"false\"").And.NotContain(Mark);
        html.Should().Contain("You already shared a food with this barcode, so this one is kept for you only.")
            .And.NotContain("Another food already shares this barcode");
    }

    [Fact]
    public async Task Another_accounts_private_food_is_not_offered()
    {
        await using var app = NewApp();
        var a = await SignInAsync(app);
        var b = await SignInAsync(app);
        var code = NewEan();
        await SaveManualAsync(a, Unique("Kefir private kbt"), code, share: false);

        (await b.Client.GetAsync(Known(code))).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task An_unknown_or_invalid_barcode_is_no_content_and_open_food_facts_is_not_asked()
    {
        var off = new FakeFoodDatabase();
        await using var app = new AppFactory(fixture, services: s => s.AddSingleton<IFoodDatabase>(off));
        var a = await SignInAsync(app);

        (await a.Client.GetAsync(Known(NewEan()))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await a.Client.GetAsync(Known("123"))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        off.Lookups.Should().Be(0);
    }

    [Fact]
    public async Task Saving_privately_after_rejecting_a_shared_match_succeeds()
    {
        // What the reject button leaves: the same barcode with share unticked (Review Focus 3).
        await using var app = NewApp();
        var a = await SignInAsync(app);
        var b = await SignInAsync(app);
        var code = NewEan();
        await SaveManualAsync(a, Unique("Boza theirs kbt"), code, share: true);

        (await SaveManualAsync(b, Unique("Boza mine kbt"), code, share: false)).StatusCode.Should().Be(HttpStatusCode.Redirect);
    }
}
