using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GymTracker.Core.Text;

/// <summary>
/// Groups occurrences of one bug: the error type, its message without ids, numbers or quoted values,
/// the first frame in the app's own code, and the page with ids replaced (SPEC 11).
/// </summary>
public static partial class ErrorFingerprint
{
    public static string Of(string type, string message, string? stack, string? path)
    {
        var key = string.Join('\n', type, NormalizeMessage(message), FirstAppFrame(stack) ?? "", PathTemplate(path));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
    }

    public static string NormalizeMessage(string message)
    {
        var text = Guid().Replace(message, "{guid}");
        text = Quoted().Replace(text, "'…'");
        return Number().Replace(text, "#");
    }

    /// <summary>The page without its query, with numeric segments as {id}: /food/entries/123 → /food/entries/{id}.</summary>
    public static string PathTemplate(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "";
        }

        var withoutQuery = path.Split('?', '#')[0];
        return string.Join('/', withoutQuery.Split('/').Select(s => s.Length > 0 && s.All(char.IsAsciiDigit) ? "{id}" : s));
    }

    /// <summary>
    /// The first stack frame in the app's code: a .NET frame in a GymTracker namespace, or a browser frame in
    /// one of the app's /js/ files, without origin, query, file path or line numbers.
    /// </summary>
    public static string? FirstAppFrame(string? stack)
    {
        if (string.IsNullOrEmpty(stack))
        {
            return null;
        }

        foreach (var raw in stack.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("at ", StringComparison.Ordinal))
            {
                line = line[3..];
            }

            if (line.StartsWith("GymTracker.", StringComparison.Ordinal))
            {
                var at = line.IndexOf(" in ", StringComparison.Ordinal);
                return at > 0 ? line[..at] : line;
            }

            if (ScriptFrame().Match(line) is { Success: true } m)
            {
                var function = m.Groups["fn"].Value.Trim().TrimEnd('(', '@').Trim();
                return $"{(function.Length > 0 ? function : "<anonymous>")} ({m.Groups["file"].Value})";
            }
        }

        return null;
    }

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex Guid();

    [GeneratedRegex(@"'[^']*'|""[^""]*""")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();

    // "render (https://host/js/nav.js?v=x:10:5)" in Chrome, "render@https://host/js/nav.js:10:5" in Safari.
    [GeneratedRegex(@"^(?<fn>[^(@]*)[(@]?\s*(?:https?://[^/]+)?(?<file>/js/[\w.-]+\.js)")]
    private static partial Regex ScriptFrame();
}
