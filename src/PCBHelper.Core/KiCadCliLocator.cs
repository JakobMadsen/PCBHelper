namespace PCBHelper.Core;

public sealed class KiCadCliLocator
{
    private readonly Func<string, string?> _getEnvironmentVariable;

    public KiCadCliLocator()
        : this(Environment.GetEnvironmentVariable)
    {
    }

    public KiCadCliLocator(Func<string, string?> getEnvironmentVariable)
    {
        _getEnvironmentVariable = getEnvironmentVariable;
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

        return new KiCadCliLocation(false, null, "PATH", "kicad-cli was not found on PATH and KICAD_CLI is not set.");
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
}

public sealed record KiCadCliLocation(bool Found, string? ExecutablePath, string Source, string? Message);
