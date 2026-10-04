using System.Globalization;
using GymTracker.Core.Domain;
using GymTracker.Web.Features.Profile;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace GymTracker.Web.Features.Localization;

/// <summary>The language and theme for this request: the account's choice when signed in, else the device's cookies.</summary>
public record UserPreferences(Theme Theme, Language Language)
{
    public static readonly UserPreferences Default = new(Theme.Dark, Language.English);

    public string ThemeAttribute => Theme.ToString().ToLowerInvariant();
    public string LangAttribute => Language == Language.Bulgarian ? "bg" : "en";
}

/// <summary>
/// Reads the signed-in user's language and theme from the profile, sets the UI culture, and mirrors both into
/// cookies so the login and offline pages on this device match. The UI culture only changes words and dates:
/// CurrentCulture stays invariant so numbers always use a dot.
/// </summary>
public class UserPreferencesMiddleware(RequestDelegate next)
{
    public const string LangCookie = "gym.lang";
    public const string ThemeCookie = "gym.theme";

    public async Task InvokeAsync(HttpContext context, ProfileService profiles)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is null)
        {
            await next(context);
            return;
        }

        var prefs = FromCookies(context.Request.Cookies);
        if (context.User.Identity?.IsAuthenticated == true)
        {
            // Loads the whole profile, which the page then reuses (ProfileService keeps it for the request).
            if (await profiles.FindAsync(context.RequestAborted) is { } profile)
            {
                prefs = new UserPreferences(profile.Theme, profile.Language);
                Remember(context.Response, context.Request.Cookies, prefs);
            }
        }

        context.Items[typeof(UserPreferences)] = prefs;
        if (prefs.Language == Language.Bulgarian)
        {
            CultureInfo.CurrentUICulture = Localizer.Bulgarian;
        }

        await next(context);
    }

    public static UserPreferences FromCookies(IRequestCookieCollection cookies) => new(
        Enum.TryParse<Theme>(cookies[ThemeCookie], true, out var theme) ? theme : UserPreferences.Default.Theme,
        cookies[LangCookie] == "bg" ? Language.Bulgarian : Language.English);

    public static void Remember(HttpResponse response, IRequestCookieCollection current, UserPreferences prefs)
    {
        var options = new CookieOptions { MaxAge = TimeSpan.FromDays(400), SameSite = SameSiteMode.Lax, Secure = true, HttpOnly = false, IsEssential = true };
        if (current[LangCookie] != prefs.LangAttribute)
        {
            response.Cookies.Append(LangCookie, prefs.LangAttribute, options);
        }

        if (current[ThemeCookie] != prefs.ThemeAttribute)
        {
            response.Cookies.Append(ThemeCookie, prefs.ThemeAttribute, options);
        }
    }
}

public static class UserPreferencesExtensions
{
    public static UserPreferences Preferences(this HttpContext context) =>
        context.Items[typeof(UserPreferences)] as UserPreferences ?? UserPreferences.Default;
}
