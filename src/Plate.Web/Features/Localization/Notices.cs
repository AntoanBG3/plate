using Plate.Core.Text;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Plate.Web.Features.Localization;

/// <summary>
/// TempData messages carried across a redirect as format plus arguments, so the next page shows them in
/// the user's language. Plain strings stored by older code still work as keys.
/// </summary>
public static class Notices
{
    public const string MessageKey = "Message";
    private const char Separator = '\u001f';

    public static void Notice(this ITempDataDictionary temp, LocalText text) => temp[MessageKey] = Pack(text);

    public static void Error(this ITempDataDictionary temp, LocalText text) => temp[FeatureExceptionFilter.ErrorKey] = Pack(text);

    public const string InfoKey = "Info";

    /// <summary>A neutral note on the next page: nothing went wrong, but nothing happened either ("Nothing was logged…").</summary>
    public static void Info(this ITempDataDictionary temp, LocalText text) => temp[InfoKey] = Pack(text);

    public const string FocusKey = "Focus";

    /// <summary>The element (by id) the next page scrolls to, so a change made far down the page stays in view.</summary>
    public static void Focus(this ITempDataDictionary temp, string elementId) => temp[FocusKey] = elementId;

    public static string? ReadFocus(ITempDataDictionary temp) => temp[FocusKey] as string;

    public const string UndoKey = "Undo";

    /// <summary>Shows text with an Undo button on the next page; url is the POST that restores the row.</summary>
    public static void Undo(this ITempDataDictionary temp, LocalText text, string url) =>
        temp[UndoKey] = Pack(text) + Separator + Separator + url;

    public static (LocalText Text, string Url)? ReadUndo(ITempDataDictionary temp)
    {
        if (temp[UndoKey] is not string s)
        {
            return null;
        }

        var at = s.LastIndexOf($"{Separator}{Separator}", StringComparison.Ordinal);
        return at < 0 ? null : (Read(s[..at])!, s[(at + 2)..]);
    }

    public static LocalText? Read(object? value) => value is string s
        ? s.Split(Separator) is var parts && parts.Length > 1 ? new LocalText(parts[0], parts[1..]) : new LocalText(s)
        : null;

    private static string Pack(LocalText text) =>
        string.Join(Separator, new[] { text.Format }.Concat(text.Args.Select(a => Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture) ?? "")));
}
