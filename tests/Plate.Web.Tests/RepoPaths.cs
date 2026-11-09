namespace Plate.Web.Tests;

/// <summary>Source paths for tests that read the repository's own files.</summary>
internal static class RepoPaths
{
    public static readonly string Root = FindRoot();
    public static readonly string Web = Path.Combine(Root, "src", "Plate.Web");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Plate.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
