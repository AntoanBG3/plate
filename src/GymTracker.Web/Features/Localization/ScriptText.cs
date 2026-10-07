namespace GymTracker.Web.Features.Localization;

/// <summary>
/// Strings the browser scripts show, passed to them translated in a JSON block in the layout.
/// Scripts look them up with t("..."); a test checks every t("...") in wwwroot/js is listed here.
/// </summary>
public static class ScriptText
{
    public static readonly string[] Keys =
    [
        // site.js
        "Something went wrong ({0}). Try again.",
        "No connection. That change was not saved; try again when you have signal.",
        "{0} kcal · {1} g protein",
        "OK",
        "Add",
        "Add {0}",
        "Using: {0}",
        "Database",
        // session.js
        "1 set is saved on this phone and will send when the connection is back.",
        "{0} sets are saved on this phone and will send when the connection is back.",
        "That set was not valid.",
        "That set's exercise no longer exists.",
        "Rest over",
        "Waiting for signal",
        "Rest",
        "Next: {0}",
        "{0} kg × {1}",
        // motion.js
        "Undo",
        "Set deleted.",
        "Undo didn't go through.",
        "Try again",
        // scanner.js
        "This browser can't open the camera. Type the digits or take a photo instead.",
        "Camera access was refused. Type the digits or take a photo instead.",
        "Point the camera at the barcode.",
        "Found {0}. Looking it up…",
        "Reading the photo…",
        "No barcode found in that photo. Try again closer, or type the digits.",
        "Couldn't read that photo. Type the digits instead.",
        // progress.js
        "Weigh-in",
        "Trend",
        "Eaten",
        "Target",
        "Maintenance",
        "This week",
        "Last week",
        "{0} kg",
        "{0} kcal",
        "{0} sets",
        "Aim",
        "Minimum",
        "{0} g",
        // feedback.js
        "Report",
        "Thanks!",
        "Thanks. What was wrong?",
        "Thanks. This helps it read food better.",
        "Couldn't read that picture. Try a screenshot.",
        "No connection. Remove the screenshot to keep the message on this phone, or try again with signal.",
        // passkey.js
        "Couldn't add the passkey. Try again.",
        "That passkey didn't work. Try again or use your password.",
        "That passkey didn't work. Try again.",
        // label.js
        "Needs a connection",
        "Couldn't read that picture. Try another photo.",
        "Too many photos for now. Try again in a few minutes.",
        "Couldn't read the label. Enter the values by hand.",
    ];

    public static Dictionary<string, string> For(Localizer localizer) =>
        Localizer.IsBulgarian ? Keys.ToDictionary(k => k, k => localizer[k]) : [];
}
