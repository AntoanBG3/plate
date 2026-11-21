using Plate.Core.Domain;
using Plate.Web.Tests;
using Microsoft.EntityFrameworkCore;
using FluentAssertions;

namespace Plate.E2E.Tests;

/// <summary>The exercise picker and custom exercises (SPEC 7, Phase 11).</summary>
public class ExercisePickerTests(E2EFixture app) : IClassFixture<E2EFixture>
{
    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Several_exercises_are_added_at_once_in_the_order_tapped(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press");
        await phone.GoAsync($"/train/templates/{template}/add-exercise");

        var bench = phone.Page.Locator("[data-filter-item]").Filter(new() { HasText = "Bench press" }).First;
        (await bench.Locator("[data-added]").CountAsync()).Should().Be(1, "exercises already in the template are marked");

        await phone.Page.Locator("[data-filter-item]").Filter(new() { HasText = "Barbell row" }).First.TapAsync();
        await phone.Page.Locator("[data-filter-item]").Filter(new() { HasText = "Overhead press" }).First.TapAsync();
        await phone.Page.Locator("[data-filter-item]").Filter(new() { HasText = "Dip" }).First.TapAsync();
        await phone.Page.Locator("[data-filter-item]").Filter(new() { HasText = "Overhead press" }).First.TapAsync(); // changed my mind
        (await phone.Page.Locator("[data-pick-submit]").TextContentAsync())!.Trim().Should().Be("Add 2");

        await phone.NavigateByAsync(() => phone.Page.Locator("[data-pick-submit]").TapAsync());
        phone.Page.Url.Should().EndWith($"/train/templates/{template}");
        var names = (await phone.Page.Locator(".exercise .ex-name").AllTextContentsAsync()).Select(n => n.Trim());
        names.Should().Equal("Bench press", "Barbell row", "Dip");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_search_with_no_match_creates_the_exercise_and_adds_it(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var template = await phone.CreateTemplateAsync(app, "Upper", "Bench press");
        await phone.GoAsync($"/train/templates/{template}/add-exercise");

        await phone.Page.Locator("[data-filter]").FillAsync("Landmine press");
        await phone.NavigateByAsync(() => phone.Page.Locator("[data-create-exercise]").TapAsync());
        (await phone.Page.Locator("input[name=name]").InputValueAsync()).Should().Be("Landmine press");
        await phone.Page.Locator("select[name=primaryMuscle]").SelectOptionAsync("Shoulders");
        await phone.Page.Locator("select[name=equipment]").SelectOptionAsync("Barbell");
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action^='/settings/exercises'] button[type=submit]").TapAsync());

        phone.Page.Url.Should().EndWith($"/train/templates/{template}");
        (await phone.Page.Locator(".exercise .ex-name").AllTextContentsAsync()).Select(n => n.Trim()).Should().Contain("Landmine press");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Train_with_no_templates_offers_one_way_to_make_one(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/train");
        (await phone.Page.Locator(".section-aside[href='/train/templates']").CountAsync()).Should().Be(0, "nothing to edit yet");
        (await phone.Page.Locator("a.row-link[href='/train/templates']").CountAsync()).Should().Be(0);
        (await phone.Page.Locator(".card.empty a[href='/train/templates']").CountAsync()).Should().Be(1);

        await phone.CreateTemplateAsync(app, "Upper", "Bench press");
        await phone.GoAsync("/train");
        (await phone.Page.Locator(".section-aside[href='/train/templates']").CountAsync()).Should().Be(1);
        (await phone.Page.Locator("a.row-link[href='/train/templates']").CountAsync()).Should().Be(1);
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Custom_exercises_are_renamed_and_archived_in_settings(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync("/settings");
        await phone.NavigateByAsync(() => phone.Page.Locator("a[href='/settings/exercises']").TapAsync());
        await phone.NavigateByAsync(() => phone.Page.Locator("a[href^='/settings/exercises/new']").TapAsync());
        await phone.Page.Locator("input[name=name]").FillAsync("Sled push");
        await phone.Page.Locator("select[name=primaryMuscle]").SelectOptionAsync("Quads");
        await phone.Page.Locator("select[name=equipment]").SelectOptionAsync("Other");
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action^='/settings/exercises'] button[type=submit]").TapAsync());

        await phone.NavigateByAsync(() => phone.Page.Locator("a.row-link").Filter(new() { HasText = "Sled push" }).TapAsync());
        await phone.Page.Locator("input[name=name]").FillAsync("Sled push, heavy");
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action$='/rename'] button[type=submit]").TapAsync());
        (await phone.Page.Locator("a.row-link").Filter(new() { HasText = "Sled push, heavy" }).CountAsync()).Should().Be(1);

        await phone.NavigateByAsync(() => phone.Page.Locator("a.row-link").Filter(new() { HasText = "Sled push, heavy" }).TapAsync());
        await phone.Page.Locator("form[action$='/archive'] button").TapAsync();
        await phone.NavigateByAsync(() => phone.Page.Locator("#confirm-sheet [data-confirm-yes]").TapAsync());
        (await phone.Page.Locator("a.row-link").Filter(new() { HasText = "Sled push" }).CountAsync()).Should().Be(0);
        phone.ShouldHaveNoErrors();
    }

    [Fact]
    public async Task Archived_pick_rolls_back_the_entire_selection()
    {
        await using var phone = await app.SignedInPhoneAsync(E2EFixture.Chromium);
        var id = await phone.CreateTemplateAsync(app, "Partial pick");
        await using var db = app.Db.CreateContext(null);
        var uid = await db.Users.Where(u => u.Email == phone.Email).Select(u => u.Id).SingleAsync();
        await using var s = new UserScope(app.Db, uid);
        var stale = await s.Exercises.CreateAsync("Audit old movement", Muscle.Chest, Equipment.Other, [], default);
        var good = await app.ExerciseIdAsync("Bench press");
        await phone.GoAsync($"/train/templates/{id}/add-exercise");
        await s.Exercises.ArchiveAsync(stale.Id, default);
        var status = await phone.Page.EvaluateAsync<int>("""
            async ([path,good,stale]) => {
                const data = new URLSearchParams();
                data.append('exerciseIds', good);
                data.append('exerciseIds', stale);
                data.append('__RequestVerificationToken', window.gymToken());
                return (await fetch(path, {method:'POST',body:data})).status;
            }
            """, new object[] {$"/train/templates/{id}/exercises", good, stale.Id});
        status.Should().Be(404);
        (await s.Db.TemplateExercises.Where(x => x.TemplateId == id).Select(x => x.ExerciseId).ToListAsync()).Should().BeEmpty();
    }
}
