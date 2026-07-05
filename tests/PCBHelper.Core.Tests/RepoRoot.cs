namespace PCBHelper.Core.Tests;

internal static class RepoRoot
{
    public static string Path { get; } = Find();

    private static string Find()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(System.IO.Path.Combine(current, "PCBHelper.slnx")))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
