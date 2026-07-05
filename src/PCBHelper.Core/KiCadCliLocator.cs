namespace PCBHelper.Core;

public sealed class KiCadCliLocator
{
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<IEnumerable<string>> _getInstallRoots;

    public KiCadCliLocator()
        : this(Environment.GetEnvironmentVariable, KiCadInstallRootDiscovery.GetInstallRoots)
    {
    }

    public KiCadCliLocator(Func<string, string?> getEnvironmentVariable)
        : this(getEnvironmentVariable, static () => Array.Empty<string>())
    {
    }

    public KiCadCliLocator(Func<string, string?> getEnvironmentVariable, Func<IEnumerable<string>> getInstallRoots)
    {
        _getEnvironmentVariable = getEnvironmentVariable;
        _getInstallRoots = getInstallRoots;
    }

    public KiCadCliLocation Locate()
    {
        var configured = _getEnvironmentVariable("KICAD_CLI");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var configuredPath = Path.GetFullPath(configured);
            if (File.Exists(configuredPath))
            {
                return new KiCadCliLocation(true, configuredPath, "KICAD_CLI", null);
            }

            return new KiCadCliLocation(false, null, "KICAD_CLI", $"KICAD_CLI points to a missing file: {configuredPath}");
        }

        var path = _getEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var executableName in GetExecutableNames())
            {
                var candidate = Path.Combine(directory, executableName);
                if (File.Exists(candidate))
                {
                    return new KiCadCliLocation(true, Path.GetFullPath(candidate), "PATH", null);
                }
            }
        }

        foreach (var installRoot in _getInstallRoots())
        {
            foreach (var candidateDirectory in GetInstallCandidateDirectories(installRoot))
            {
                foreach (var executableName in GetExecutableNames())
                {
                    var candidate = Path.Combine(candidateDirectory, executableName);
                    if (File.Exists(candidate))
                    {
                        return new KiCadCliLocation(true, Path.GetFullPath(candidate), "KiCad install location", null);
                    }
                }
            }
        }

        return new KiCadCliLocation(false, null, "PATH", "kicad-cli was not found on PATH, KICAD_CLI is not set, and no KiCad install location was detected.");
    }

    private static IEnumerable<string> GetExecutableNames()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return "kicad-cli.exe";
            yield return "kicad-cli.cmd";
            yield return "kicad-cli.bat";
        }

        yield return "kicad-cli";
    }

    private static IEnumerable<string> GetInstallCandidateDirectories(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            yield break;
        }

        yield return installRoot;
        yield return Path.Combine(installRoot, "bin");
    }
}

public sealed record KiCadCliLocation(bool Found, string? ExecutablePath, string Source, string? Message);
