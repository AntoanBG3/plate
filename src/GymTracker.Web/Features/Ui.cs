using System.Globalization;
using GymTracker.Web.Features.Localization;
using Microsoft.AspNetCore.Html;

namespace GymTracker.Web.Features;

/// <summary>Small view helpers: icons from the inline sprite and dates in the user's language.</summary>
public static class Ui
{
    public static IHtmlContent Icon(string name, string? cssClass = null) =>
        new HtmlString($"<svg class=\"icon{(cssClass is null ? "" : " " + cssClass)}\" aria-hidden=\"true\" focusable=\"false\"><use href=\"#i-{name}\"/></svg>");

    /// <summary>"Saturday, 3 October" or "Събота, 3 октомври".</summary>
    public static string LongDate(DateOnly date) => Capitalize(date.ToString("dddd, d MMMM", Localizer.DateCulture));

    /// <summary>"Sat 3 Oct" or "Съб 3 окт".</summary>
    public static string ShortDate(DateOnly date) => Capitalize(date.ToString("ddd d MMM", Localizer.DateCulture).Replace(".", ""));

    /// <summary>"3 Oct" or "3 окт".</summary>
    public static string DayMonth(DateOnly date) => date.ToString("d MMM", Localizer.DateCulture).Replace(".", "");

    public static string DayMonth(DateTime date) => DayMonth(DateOnly.FromDateTime(date));

    public static string ShortDate(DateTime date) => ShortDate(DateOnly.FromDateTime(date));

    public static string Time(DateTime time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The meal most likely being logged at this local time of day.</summary>
    public static GymTracker.Core.Domain.Meal MealAt(DateTimeOffset local) => local.Hour switch
    {
        < 11 => GymTracker.Core.Domain.Meal.Breakfast,
        < 16 => GymTracker.Core.Domain.Meal.Lunch,
        >= 18 and < 23 => GymTracker.Core.Domain.Meal.Dinner,
        _ => GymTracker.Core.Domain.Meal.Snack,
    };

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0], Localizer.DateCulture) + s[1..];
}
