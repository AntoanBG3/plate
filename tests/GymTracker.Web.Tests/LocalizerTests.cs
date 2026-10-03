using System.Globalization;
using FluentAssertions;
using GymTracker.Core.Domain;
using GymTracker.Core.Text;
using GymTracker.Web.Features.Localization;
using Xunit;

namespace GymTracker.Web.Tests;

public class LocalizerTests
{
    private static readonly Localizer L = new(new Dictionary<string, string>
    {
        ["Today"] = "Днес",
        ["Hold {0} kg, add a rep"] = "Задръж {0} кг, добави повторение",
        ["Copied 1 item."] = "Копиран е 1 продукт.",
        ["Copied {0} items."] = "Копирани са {0} продукта.",
        ["Breakfast"] = "Закуска",
    });

    private static T InCulture<T>(string culture, Func<T> f)
    {
        var before = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            return f();
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    [Fact]
    public void English_returns_the_key_itself() =>
        InCulture("en", () => L["Today"]).Should().Be("Today");

    [Fact]
    public void Bulgarian_uses_the_catalogue_and_falls_back_to_English() =>
        InCulture("bg-BG", () => (L["Today"], L["Not translated yet"])).Should().Be(("Днес", "Not translated yet"));

    [Fact]
    public void Arguments_keep_invariant_numbers_in_both_languages() =>
        InCulture("bg-BG", () => L[new LocalText("Hold {0} kg, add a rep", 82.5m)]).Should().Be("Задръж 82.5 кг, добави повторение");

    [Fact]
    public void Counts_pick_the_singular_or_plural_form()
    {
        InCulture("bg-BG", () => L.Count(1, "Copied 1 item.", "Copied {0} items.")).Should().Be("Копиран е 1 продукт.");
        InCulture("bg-BG", () => L.Count(3, "Copied 1 item.", "Copied {0} items.")).Should().Be("Копирани са 3 продукта.");
        InCulture("en", () => L.Count(3, "Copied 1 item.", "Copied {0} items.")).Should().Be("Copied 3 items.");
    }

    [Fact]
    public void Enums_use_their_English_name_as_the_key() =>
        InCulture("bg-BG", () => L.Enum(Meal.Breakfast)).Should().Be("Закуска");

    [Fact]
    public void Notices_survive_a_round_trip_through_TempData_strings()
    {
        var temp = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(new Microsoft.AspNetCore.Http.DefaultHttpContext(), new NullTempDataProvider());
        temp.Notice(LocalText.Count(3, "Copied 1 item.", "Copied {0} items."));
        InCulture("bg-BG", () => L[Notices.Read(temp[Notices.MessageKey])!]).Should().Be("Копирани са 3 продукта.");
    }

    private sealed class NullTempDataProvider : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(Microsoft.AspNetCore.Http.HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(Microsoft.AspNetCore.Http.HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
