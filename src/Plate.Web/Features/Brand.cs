namespace Plate.Web.Features;

/// <summary>
/// The app's user-facing name. It is an English word kept in Latin letters in every language (a transliteration
/// would lose the weight plate / dinner plate meaning), so it is not localized. The static files that name the app
/// (both web manifests, offline.html, offline.js) spell it out themselves.
/// </summary>
public static class Brand
{
    public const string Name = "Plate";

    /// <summary>
    /// The app moved from gym.peychev.uk to plate.peychev.uk on 2026-11-09 08:30 UTC, and passkeys made on the old
    /// host don't work on the new one. The sign-in page says so for three days; delete this and the note after that.
    /// </summary>
    public static readonly DateTimeOffset MoveNoticeUntil = new(2026, 11, 12, 8, 30, 0, TimeSpan.Zero);

    public static bool ShowMoveNotice(DateTimeOffset now) => now < MoveNoticeUntil;
}
