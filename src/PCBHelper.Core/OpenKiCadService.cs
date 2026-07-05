using System.Diagnostics;

namespace PCBHelper.Core;

public sealed class KiCadExecutableLocator
{
    private readonly KiCadCliLocator _cliLocator;
    private readonly Func<string, string?> _getEnvironmentVariable;

    public KiCadExecutableLocator(KiCadCliLocator cliLocator)
        : this(cliLocator, Environment.GetEnvironmentVariable)
    {
    }

    public KiCadExecutableLocator(KiCadCliLocator cliLocator, Func<string, string?> getEnvironmentVariable)
    {
        _cliLocator = cliLocator;
        _getEnvironmentVariable = getEnvironmentVariable;
    }

    public KiCadExecutableLocation Locate()
    {
        var cli = _cliLocator.Locate();
        if (cli.Found && cli.ExecutablePath is not null)
        {
            var besideCli = Path.Combine(Path.GetDirectoryName(cli.ExecutablePath)!, GetExecutableName());
            if (File.Exists(besideCli))
            {
                return new KiCadExecutableLocation(true, Path.GetFullPath(besideCli), $"beside {Path.GetFileName(cli.ExecutablePath)}", null);
            }
        }

        var configured = _getEnvironmentVariable("KICAD");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var configuredPath = Path.GetFullPath(configured);
            if (File.Exists(configuredPath))
            {
                return new KiCadExecutableLocation(true, configuredPath, "KICAD", null);
            }
        }

        var path = _getEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, GetExecutableName());
            if (File.Exists(candidate))
            {
                return new KiCadExecutableLocation(true, Path.GetFullPath(candidate), "PATH", null);
            }
        }

        return new KiCadExecutableLocation(false, null, "PATH", "kicad.exe was not found beside kicad-cli, through KICAD, or on PATH.");
    }

    private static string GetExecutableName()
    {
        return OperatingSystem.IsWindows() ? "kicad.exe" : "kicad";
    }
}

public sealed class OpenKiCadService
{
    private readonly ProjectDiscoveryService _projectDiscovery;
    private readonly KiCadExecutableLocator _locator;
    private readonly IProcessStarter _starter;

    public OpenKiCadService(ProjectDiscoveryService projectDiscovery, KiCadExecutableLocator locator, IProcessStarter starter)
    {
        _projectDiscovery = projectDiscovery;
        _locator = locator;
        _starter = starter;
    }

    public ToolResponse<OpenProjectResult> OpenProject(string projectPath, bool dryRun)
    {
        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<OpenProjectResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        if (project.Data.ProjectFile is null)
        {
            return ToolResponse<OpenProjectResult>.Fail("open requires a .kicad_pro file.", "PROJECT_FILE_MISSING");
        }

        var executable = _locator.Locate();
        if (!executable.Found || executable.ExecutablePath is null)
        {
            return ToolResponse<OpenProjectResult>.Fail("KiCad GUI executable was not found.", "KICAD_NOT_FOUND", executable.Message);
        }

        if (dryRun)
        {
            return ToolResponse<OpenProjectResult>.Ok(
                $"Would open {project.Data.ProjectFile} in KiCad.",
                new OpenProjectResult(project.Data.ProjectFile, executable.ExecutablePath, true, false, null));
        }

        var started = _starter.Start(executable.ExecutablePath, new[] { project.Data.ProjectFile }, project.Data.ProjectRoot);
        return ToolResponse<OpenProjectResult>.Ok(
            $"Opened {project.Data.ProjectFile} in KiCad.",
            new OpenProjectResult(project.Data.ProjectFile, executable.ExecutablePath, false, true, started.ProcessId));
    }
}

public interface IProcessStarter
{
    StartedProcessResult Start(string fileName, IReadOnlyList<string> arguments, string? workingDirectory);
}

public sealed class ProcessStarter : IProcessStarter
{
    public StartedProcessResult Start(string fileName, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process: {fileName}");

        return new StartedProcessResult(process.Id);
    }
}

public sealed record KiCadExecutableLocation(bool Found, string? ExecutablePath, string Source, string? Message);

public sealed record StartedProcessResult(int? ProcessId);

public sealed record OpenProjectResult(
    string ProjectFile,
    string ExecutablePath,
    bool DryRun,
    bool Started,
    int? ProcessId);
