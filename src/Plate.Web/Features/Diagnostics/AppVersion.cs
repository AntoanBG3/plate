using System.Reflection;

namespace Plate.Web.Features.Diagnostics;

/// <summary>
/// The Git commit the app was built from: APP_VERSION passed to the Docker build as the informational version,
/// or, in a local build, the commit the SDK appends after "+". Ties reports and errors to a commit (SPEC 11).
/// </summary>
public static class AppVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var value = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var plus = value.IndexOf('+');
        var version = plus >= 0 ? value[(plus + 1)..] : value;
        return version.Length > 12 && version.All(char.IsAsciiHexDigit) ? version[..7] : version;
    }
}
