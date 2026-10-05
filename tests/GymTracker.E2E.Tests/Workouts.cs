using GymTracker.Web.Tests;
using Microsoft.EntityFrameworkCore;

namespace GymTracker.E2E.Tests;

/// <summary>Sets up templates and sessions through the app's own endpoints, as the signed-in phone.</summary>
public static class Workouts
{
    /// <summary>POSTs a form from inside the page (same cookies, the page's anti-forgery token) and returns the final URL.</summary>
    public static async Task<string> PostAsync(this Phone phone, string path, Dictionary<string, string>? fields = null)
    {
        return await phone.Page.EvaluateAsync<string>("""
            async ([path, fields]) => {
                const body = new URLSearchParams(fields || {});
                body.set("__RequestVerificationToken", window.gymToken());
                const response = await fetch(path, { method: "POST", body, credentials: "same-origin" });
                if (!response.ok) {
                    throw new Error(`${path}: ${response.status}`);
                }
                return response.url;
            }
            """, new object?[] { path, fields ?? [] });
    }

    public static async Task<int> ExerciseIdAsync(this E2EFixture app, string name)
    {
        await using var db = app.Db.CreateContext(null);
        return await db.Exercises.IgnoreQueryFilters().Where(e => e.Name == name && e.OwnerUserId == null).Select(e => e.Id).SingleAsync();
    }

    /// <summary>A template with the given exercises, in order. Returns its id.</summary>
    public static async Task<int> CreateTemplateAsync(this Phone phone, E2EFixture app, string name, params string[] exercises)
    {
        await phone.GoAsync("/train/templates");
        var url = await phone.PostAsync("/train/templates", new() { ["name"] = name });
        var id = int.Parse(new Uri(url).Segments[^1]);
        foreach (var exercise in exercises)
        {
            await phone.PostAsync($"/train/templates/{id}/exercises", new() { ["exerciseId"] = (await app.ExerciseIdAsync(exercise)).ToString() });
        }

        return id;
    }

    /// <summary>Starts a session from the template and opens it. Returns the session id.</summary>
    public static async Task<int> StartSessionAsync(this Phone phone, int templateId)
    {
        var url = await phone.PostAsync("/train/start", new() { ["templateId"] = templateId.ToString() });
        var id = int.Parse(new Uri(url).Segments[^1]);
        await phone.GoAsync($"/train/sessions/{id}");
        return id;
    }

    /// <summary>Types weight and reps into a set row and taps its check button, then waits for the server's row.</summary>
    public static async Task LogSetAsync(this Phone phone, Microsoft.Playwright.ILocator row, string kg, string reps)
    {
        await row.Locator("input[name=weightKg]").FillAsync(kg);
        await row.Locator("input[name=reps]").FillAsync(reps);
        await row.Locator(".done-btn").TapAsync();
    }
}
