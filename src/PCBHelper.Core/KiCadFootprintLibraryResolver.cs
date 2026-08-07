namespace PCBHelper.Core;

internal static class KiCadFootprintLibraryResolver
{
    internal const string RootEnvironmentVariable = "PCBHELPER_KICAD_FOOTPRINT_ROOTS";

    public static string? Resolve(string footprint, string? configuredRoots = null)
    {
        var separator = footprint.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == footprint.Length - 1)
        {
            return null;
        }

        var library = footprint[..separator];
        var name = footprint[(separator + 1)..];
        if (ContainsDirectorySeparator(library) || ContainsDirectorySeparator(name))
        {
            return null;
        }

        foreach (var root in GetSearchRoots(configuredRoots))
        {
            var candidate = Path.Combine(root, $"{library}.pretty", $"{name}.kicad_mod");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    internal static IReadOnlyList<string> GetSearchRoots(string? configuredRoots = null)
    {
        var roots = new List<string>();
        var explicitRoots = configuredRoots ?? Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        AddPathList(roots, explicitRoots);
        if (roots.Count > 0)
        {
            return DistinctRoots(roots);
        }

        AddRoot(roots, Environment.GetEnvironmentVariable("KICAD10_FOOTPRINT_DIR"));

        AddSpecialFolderRoot(roots, Environment.SpecialFolder.ProgramFiles);
        AddSpecialFolderRoot(roots, Environment.SpecialFolder.ProgramFilesX86);

        AddRoot(roots, @"D:\Program Files\KiCad\10.0\share\kicad\footprints");
        AddRoot(roots, "/usr/share/kicad/footprints");
        AddRoot(roots, "/usr/local/share/kicad/footprints");
        AddRoot(roots, "/Applications/KiCad/KiCad.app/Contents/SharedSupport/footprints");

        return DistinctRoots(roots);
    }

    private static void AddPathList(ICollection<string> roots, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (var root in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AddRoot(roots, root);
        }
    }

    private static void AddRoot(ICollection<string> roots, string? root)
    {
        if (!string.IsNullOrWhiteSpace(root))
        {
            roots.Add(root);
        }
    }

    private static void AddSpecialFolderRoot(ICollection<string> roots, Environment.SpecialFolder folder)
    {
        var basePath = Environment.GetFolderPath(folder);
        if (!string.IsNullOrWhiteSpace(basePath))
        {
            AddRoot(roots, Path.Combine(basePath, "KiCad", "10.0", "share", "kicad", "footprints"));
        }
    }

    private static IReadOnlyList<string> DistinctRoots(IEnumerable<string> roots) =>
        roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool ContainsDirectorySeparator(string value) =>
        value.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || value.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
}
