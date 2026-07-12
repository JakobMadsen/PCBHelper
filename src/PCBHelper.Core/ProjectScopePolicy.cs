namespace PCBHelper.Core;

public sealed class ProjectScopePolicy
{
    private readonly IReadOnlyList<string> _allowedRoots;

    private ProjectScopePolicy(bool configured, bool unrestricted, IReadOnlyList<string> allowedRoots)
    {
        IsConfigured = configured;
        IsUnrestricted = unrestricted;
        _allowedRoots = allowedRoots;
    }

    public bool IsConfigured { get; }

    public bool IsUnrestricted { get; }

    public IReadOnlyList<string> AllowedRoots => _allowedRoots;

    public static ProjectScopePolicy Unrestricted()
    {
        return new ProjectScopePolicy(configured: true, unrestricted: true, Array.Empty<string>());
    }

    public static ProjectScopePolicy FromEnvironment(Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var configured = getEnvironmentVariable("PCBHELPER_ALLOWED_ROOTS");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return new ProjectScopePolicy(configured: false, unrestricted: false, Array.Empty<string>());
        }

        var roots = configured
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ResolvePhysicalPath)
            .Distinct(PathComparer)
            .ToArray();
        return new ProjectScopePolicy(configured: roots.Length > 0, unrestricted: false, roots);
    }

    public ToolResponse<string> Authorize(string path)
    {
        if (!IsConfigured)
        {
            return ToolResponse<string>.Fail(
                "PCBHelper MCP project scope is not configured. Set PCBHELPER_ALLOWED_ROOTS to one or more semicolon-separated directories.",
                "PROJECT_SCOPE_NOT_CONFIGURED");
        }

        var resolved = ResolvePhysicalPath(path);
        if (IsUnrestricted || _allowedRoots.Any(root => IsWithin(root, resolved)))
        {
            return ToolResponse<string>.Ok("Project path is authorized.", resolved);
        }

        return ToolResponse<string>.Fail(
            $"Project path is outside PCBHelper's authorized roots: {resolved}",
            "PROJECT_SCOPE_VIOLATION");
    }

    public static bool IsWithin(string root, string candidate)
    {
        var resolvedRoot = ResolvePhysicalPath(root);
        var resolvedCandidate = ResolvePhysicalPath(candidate);
        var relative = Path.GetRelativePath(resolvedRoot, resolvedCandidate);
        return relative == "."
            || (!Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }

    public static string ResolvePhysicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return fullPath;
        }

        var current = root;
        var remainder = fullPath[root.Length..]
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in remainder)
        {
            current = Path.Combine(current, segment);
            FileSystemInfo? info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current) ? new FileInfo(current) : null;
            if (info?.LinkTarget is not null)
            {
                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
            }
        }

        return Path.GetFullPath(current);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
