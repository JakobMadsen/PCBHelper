using System.Globalization;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PCBHelper.Core;

public sealed class AutoroutingService
{
    private static readonly TimeSpan DefaultFreeRoutingTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan KiCadPythonTimeout = TimeSpan.FromSeconds(15);
    private readonly ProjectDiscoveryService _projectDiscovery;
    private readonly KiCadCliLocator _kiCadCliLocator;
    private readonly FreeRoutingLocator _freeRoutingLocator;
    private readonly ICommandRunner _runner;
    private readonly TimeSpan _freeRoutingTimeout;

    public AutoroutingService(ProjectDiscoveryService projectDiscovery, KiCadCliLocator kiCadCliLocator, ICommandRunner runner)
        : this(projectDiscovery, kiCadCliLocator, new FreeRoutingLocator(), runner)
    {
    }

    public AutoroutingService(
        ProjectDiscoveryService projectDiscovery,
        KiCadCliLocator kiCadCliLocator,
        FreeRoutingLocator freeRoutingLocator,
        ICommandRunner runner,
        TimeSpan? freeRoutingTimeout = null)
    {
        _projectDiscovery = projectDiscovery;
        _kiCadCliLocator = kiCadCliLocator;
        _freeRoutingLocator = freeRoutingLocator;
        _runner = runner;
        _freeRoutingTimeout = freeRoutingTimeout ?? DefaultFreeRoutingTimeout;
    }

    public async Task<ToolResponse<AutorouteBoardResult>> AutorouteBoardAsync(
        string projectPath,
        bool dryRun,
        CancellationToken cancellationToken = default)
        => await AutorouteBoardAsync(projectPath, dryRun, ripupExisting: false, cancellationToken);

    public async Task<ToolResponse<AutorouteBoardResult>> AutorouteBoardAsync(
        string projectPath,
        bool dryRun,
        bool ripupExisting,
        CancellationToken cancellationToken = default)
    {
        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<AutorouteBoardResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        if (project.Data.BoardFile is null)
        {
            return ToolResponse<AutorouteBoardResult>.Fail("Autoroute requires a .kicad_pcb file.", "BOARD_FILE_MISSING");
        }

        var kiCad = _kiCadCliLocator.Locate();
        var freeRouting = _freeRoutingLocator.Locate();
        var java = _freeRoutingLocator.LocateJava();
        var discovery = new AutoroutingBackendDiscovery(kiCad, freeRouting, java);
        var unavailable = GetUnavailableReason(discovery);
        if (unavailable is not null)
        {
            return ToolResponse<AutorouteBoardResult>.Fail(
                "Routing backend is unavailable.",
                "ROUTING_BACKEND_UNAVAILABLE",
                unavailable);
        }

        if (!File.ReadAllText(project.Data.BoardFile).Contains("Edge.Cuts", StringComparison.Ordinal))
        {
            return ToolResponse<AutorouteBoardResult>.Fail(
                "Autoroute requires a board outline on Edge.Cuts.",
                "BOARD_OUTLINE_MISSING",
                project.Data.BoardFile);
        }

        var root = Path.Combine(
            project.Data.ProjectRoot,
            ".pcbhelper",
            "routing",
            DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture));

        var dsnPath = Path.Combine(root, "board.dsn");
        var sesPath = Path.Combine(root, "board.ses");
        var metadataPath = Path.Combine(root, "autoroute.json");

        if (dryRun)
        {
            return ToolResponse<AutorouteBoardResult>.Ok(
                "Autoroute backend is available.",
                new AutorouteBoardResult(dryRun, project.Data.BoardFile, root, dsnPath, sesPath, metadataPath, discovery, Array.Empty<string>(), null));
        }

        Directory.CreateDirectory(root);
        var generated = new List<string>();
        if (ripupExisting)
        {
            var pythonPath = ResolveKiCadPythonPath(kiCad.ExecutablePath!);
            if (pythonPath is null)
                return ToolResponse<AutorouteBoardResult>.Fail("KiCad Python is required to rip up existing tracks safely.", "ROUTING_BACKEND_UNAVAILABLE");
            var ripupScriptPath = Path.Combine(root, "kicad-ripup.py");
            await File.WriteAllTextAsync(ripupScriptPath, BuildPythonRipupScript(project.Data.BoardFile), cancellationToken);
            var ripup = await RunKiCadPythonAsync(pythonPath, ripupScriptPath, project.Data.ProjectRoot, "KiCad Python track ripup", cancellationToken);
            await WriteCommandLogAsync(root, "kicad-ripup", ripup, cancellationToken);
            if (ripup.ExitCode != 0)
                return ToolResponse<AutorouteBoardResult>.Fail("Could not remove existing tracks before full reroute.", "ROUTING_BACKEND_UNAVAILABLE", ripup.StandardError);
        }
        var export = await _runner.RunAsync(
            kiCad.ExecutablePath!,
            new[] { "pcb", "export", "dsn", "--output", dsnPath, project.Data.BoardFile },
            project.Data.ProjectRoot,
            cancellationToken);
        await WriteCommandLogAsync(root, "kicad-export-dsn", export, cancellationToken);
        generated.Add(Path.Combine(root, "kicad-export-dsn.stdout.txt"));
        generated.Add(Path.Combine(root, "kicad-export-dsn.stderr.txt"));

        if (export.ExitCode != 0 || !File.Exists(dsnPath))
        {
            var pythonPath = ResolveKiCadPythonPath(kiCad.ExecutablePath!);
            if (pythonPath is not null)
            {
                var pythonScript = BuildPythonDsnExportScript(project.Data.BoardFile, dsnPath);
                var pythonScriptPath = Path.Combine(root, "kicad-export-dsn.py");
                await File.WriteAllTextAsync(pythonScriptPath, pythonScript, cancellationToken);
                var pythonExport = await RunKiCadPythonAsync(
                    pythonPath,
                    pythonScriptPath,
                    project.Data.ProjectRoot,
                    "KiCad Python DSN export",
                    cancellationToken);
                await WriteCommandLogAsync(root, "kicad-export-dsn-python", pythonExport, cancellationToken);
                generated.Add(Path.Combine(root, "kicad-export-dsn-python.stdout.txt"));
                generated.Add(Path.Combine(root, "kicad-export-dsn-python.stderr.txt"));

                // pcbnew may finish writing the DSN but keep native GUI threads alive.
                // The file is the authoritative completion signal after the bounded run.
                if (!File.Exists(dsnPath))
                {
                    return ToolResponse<AutorouteBoardResult>.Fail(
                        "KiCad DSN export failed.",
                        "ROUTING_BACKEND_UNAVAILABLE",
                        string.Join(Environment.NewLine, new[]
                        {
                            "kicad-cli export failed:",
                            export.StandardError,
                            "kicad python export failed:",
                            pythonExport.StandardError
                        }));
                }
            }
            else
            {
                return ToolResponse<AutorouteBoardResult>.Fail(
                    "KiCad DSN export failed.",
                    "ROUTING_BACKEND_UNAVAILABLE",
                    string.Join(Environment.NewLine, new[]
                    {
                        "kicad-cli export failed:",
                        export.StandardError,
                        "KiCad Python fallback not found (expected python executable next to kicad-cli)."
                    }));
            }
        }

        generated.Add(dsnPath);
        var routeDsnPath = Path.GetFileName(dsnPath);
        var routeSesPath = Path.GetFileName(sesPath);
        var routeCommand = BuildFreeRoutingCommand(freeRouting, java, routeDsnPath, routeSesPath);
        var route = await RunWithTimeoutAsync(routeCommand.FileName, routeCommand.Arguments, root, "FreeRouting", _freeRoutingTimeout, cancellationToken);
        await WriteCommandLogAsync(root, "freerouting", route, cancellationToken);
        generated.Add(Path.Combine(root, "freerouting.stdout.txt"));
        generated.Add(Path.Combine(root, "freerouting.stderr.txt"));
        if (route.ExitCode != 0 || !File.Exists(sesPath))
        {
            return ToolResponse<AutorouteBoardResult>.Fail(
                "FreeRouting failed.",
                "ROUTING_BACKEND_UNAVAILABLE",
                route.StandardError);
        }

        generated.Add(sesPath);
        var import = await _runner.RunAsync(
            kiCad.ExecutablePath!,
            new[] { "pcb", "import", "ses", sesPath, project.Data.BoardFile },
            project.Data.ProjectRoot,
            cancellationToken);
        await WriteCommandLogAsync(root, "kicad-import-ses", import, cancellationToken);
        generated.Add(Path.Combine(root, "kicad-import-ses.stdout.txt"));
        generated.Add(Path.Combine(root, "kicad-import-ses.stderr.txt"));
        if (import.ExitCode != 0)
        {
            var pythonPath = ResolveKiCadPythonPath(kiCad.ExecutablePath!);
            if (pythonPath is not null)
            {
                var pythonScript = BuildPythonSesImportScript(project.Data.BoardFile, sesPath);
                var pythonScriptPath = Path.Combine(root, "kicad-import-ses.py");
                await File.WriteAllTextAsync(pythonScriptPath, pythonScript, cancellationToken);
                var boardBeforeImport = File.ReadAllText(project.Data.BoardFile);
                var pythonImport = await RunKiCadPythonAsync(
                    pythonPath,
                    pythonScriptPath,
                    project.Data.ProjectRoot,
                    "KiCad Python SES import",
                    cancellationToken);
                await WriteCommandLogAsync(root, "kicad-import-ses-python", pythonImport, cancellationToken);
                generated.Add(Path.Combine(root, "kicad-import-ses-python.stdout.txt"));
                generated.Add(Path.Combine(root, "kicad-import-ses-python.stderr.txt"));

                var importChangedBoard = !string.Equals(boardBeforeImport, File.ReadAllText(project.Data.BoardFile), StringComparison.Ordinal);
                if (pythonImport.ExitCode != 0 && !importChangedBoard)
                {
                    return ToolResponse<AutorouteBoardResult>.Fail(
                        "KiCad SES import failed.",
                        "ROUTING_BACKEND_UNAVAILABLE",
                        string.Join(Environment.NewLine, new[]
                        {
                            "kicad-cli import failed:",
                            import.StandardOutput,
                            import.StandardError,
                            "kicad python import failed:",
                            pythonImport.StandardOutput,
                            pythonImport.StandardError
                        }));
                }
            }
            else
            {
                return ToolResponse<AutorouteBoardResult>.Fail(
                    "KiCad SES import failed.",
                    "ROUTING_BACKEND_UNAVAILABLE",
                    string.Join(Environment.NewLine, new[]
                    {
                        "kicad-cli import failed:",
                        import.StandardOutput,
                        import.StandardError,
                        "KiCad Python fallback not found (expected python executable next to kicad-cli)."
                    }));
            }
        }

        var result = new AutorouteBoardResult(false, project.Data.BoardFile, root, dsnPath, sesPath, metadataPath, discovery, generated, "Autoroute completed; run DRC before manufacturing.");
        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), cancellationToken);
        generated.Add(metadataPath);

        return ToolResponse<AutorouteBoardResult>.Ok("Autoroute completed.", result);
    }

    private static string? GetUnavailableReason(AutoroutingBackendDiscovery discovery)
    {
        if (!discovery.KiCadCli.Found)
        {
            return discovery.KiCadCli.Message ?? "kicad-cli was not found.";
        }

        if (!discovery.FreeRouting.Found)
        {
            return discovery.FreeRouting.Message ?? "FreeRouting was not found. Set FREEROUTING_JAR or FREEROUTING_EXE.";
        }

        if (discovery.FreeRouting.ExecutableType == FreeRoutingExecutableType.Jar && !discovery.Java.Found)
        {
            return discovery.Java.Message ?? "Java was not found. Set JAVA_HOME or add java to PATH.";
        }

        return null;
    }

    internal static AutoroutingCommand BuildFreeRoutingCommand(
        FreeRoutingLocation freeRouting,
        JavaLocation java,
        string dsnFileName,
        string sesFileName)
    {
        var freeRoutingArguments = new[] { "-de", dsnFileName, "-do", sesFileName };
        if (freeRouting.ExecutableType == FreeRoutingExecutableType.Jar)
        {
            return new AutoroutingCommand(
                java.ExecutablePath!,
                new[] { "-Djava.awt.headless=true", "-jar", freeRouting.ExecutablePath! }.Concat(freeRoutingArguments).ToArray());
        }

        return new AutoroutingCommand(freeRouting.ExecutablePath!, freeRoutingArguments);
    }

    private async Task<CommandExecutionResult> RunWithTimeoutAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string displayName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runTask = _runner.RunAsync(fileName, arguments, workingDirectory, timeoutCts.Token);
        var completed = await Task.WhenAny(runTask, Task.Delay(timeout, cancellationToken));
        if (completed == runTask)
        {
            return await runTask;
        }

        timeoutCts.Cancel();
        // A custom or third-party runner may fail to observe cancellation. Do
        // not turn the timeout itself into an unbounded wait; the production
        // runner still kills its process tree when it observes the token.
        _ = runTask.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return new CommandExecutionResult(
            -1,
            string.Empty,
            $"{displayName} timed out after {timeout.TotalSeconds:0} seconds. The process may be waiting for GUI or interactive input.");
    }

    internal async Task<CommandExecutionResult> RunKiCadPythonAsync(
        string pythonPath,
        string scriptPath,
        string workingDirectory,
        string displayName,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return await RunWithTimeoutAsync(pythonPath, new[] { scriptPath }, workingDirectory, displayName, KiCadPythonTimeout, cancellationToken);

        // pcbnew's Windows runtime can stall when its stdout/stderr handles are
        // redirected directly by a .NET parent. A non-interactive cmd wrapper
        // gives it ordinary inherited handles while PCBHelper still captures the
        // wrapper output and retains cancellation/timeout control.
        var scriptDir = Path.GetDirectoryName(scriptPath)!;
        var scriptBase = Path.GetFileNameWithoutExtension(scriptPath);
        var wrapperPath = Path.Combine(scriptDir, $"run-{scriptBase}.cmd");
        var stdoutLogPath = Path.Combine(scriptDir, $"run-{scriptBase}.stdout.log");
        var stderrLogPath = Path.Combine(scriptDir, $"run-{scriptBase}.stderr.log");
        await File.WriteAllTextAsync(wrapperPath,
            $"@echo off{Environment.NewLine}\"{pythonPath}\" \"{scriptPath}\" >\"{stdoutLogPath}\" 2>\"{stderrLogPath}\"{Environment.NewLine}",
            cancellationToken);
        var comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var startInfo = new ProcessStartInfo
        {
            FileName = comSpec,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(wrapperPath);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {displayName}.");
        var wait = process.WaitForExitAsync(cancellationToken);
        var completed = await Task.WhenAny(wait, Task.Delay(KiCadPythonTimeout, cancellationToken));
        if (completed != wait)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            return new CommandExecutionResult(-1, string.Empty, $"{displayName} timed out after {KiCadPythonTimeout.TotalSeconds:0} seconds.");
        }
        await wait;
        var stdoutLog = File.Exists(stdoutLogPath) ? await File.ReadAllTextAsync(stdoutLogPath, CancellationToken.None) : string.Empty;
        var stderrLog = File.Exists(stderrLogPath) ? await File.ReadAllTextAsync(stderrLogPath, CancellationToken.None) : string.Empty;
        return new CommandExecutionResult(process.ExitCode, stdoutLog, stderrLog);
    }

    private static async Task WriteCommandLogAsync(string root, string name, CommandExecutionResult result, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(root, $"{name}.stdout.txt"), result.StandardOutput, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, $"{name}.stderr.txt"), result.StandardError, cancellationToken);
    }

    private static string? ResolveKiCadPythonPath(string kiCadCliPath)
    {
        var directory = Path.GetDirectoryName(kiCadCliPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var candidates = OperatingSystem.IsWindows()
            ? new[] { "python.exe", "pythonw.exe" }
            : new[] { "python3", "python" };
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(directory, candidate);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string BuildPythonDsnExportScript(string boardFile, string dsnPath)
    {
        // KiCad's Windows Python runtime can keep GUI/native threads alive after
        // pcbnew finishes. Flush the generated file and exit the process without
        // waiting for native module teardown.
        return $"import pcbnew,os,sys,threading; threading.Timer(5.0,lambda:os._exit(0)).start(); b=pcbnew.LoadBoard({ToPythonStringLiteral(boardFile)}); pcbnew.ExportSpecctraDSN(b, {ToPythonStringLiteral(dsnPath)}); sys.stdout.flush(); sys.stderr.flush(); os._exit(0)";
    }

    private static string BuildPythonRipupScript(string boardFile)
    {
        return $"import pcbnew,os,sys; p={ToPythonStringLiteral(boardFile)}; b=pcbnew.LoadBoard(p); [b.Remove(t) for t in list(b.GetTracks())]; pcbnew.SaveBoard(p,b); sys.stdout.flush(); sys.stderr.flush(); os._exit(0)";
    }

    private static string BuildPythonSesImportScript(string boardFile, string sesPath)
    {
        return $"import pcbnew,os,sys,threading; threading.Timer(5.0,lambda:os._exit(0)).start(); p={ToPythonStringLiteral(boardFile)}; b=pcbnew.LoadBoard(p); ok=pcbnew.ImportSpecctraSES(b, {ToPythonStringLiteral(sesPath)}); pcbnew.SaveBoard(p, b) if ok else None; sys.stdout.flush(); sys.stderr.flush(); os._exit(0 if ok else 1)";
    }

    private static string ToPythonStringLiteral(string value)
    {
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}

public sealed class FreeRoutingLocator
{
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<string> _getToolsRoot;

    public FreeRoutingLocator()
        : this(Environment.GetEnvironmentVariable)
    {
    }

    public FreeRoutingLocator(Func<string, string?> getEnvironmentVariable)
        : this(getEnvironmentVariable, GetDefaultToolsRoot)
    {
    }

    public FreeRoutingLocator(Func<string, string?> getEnvironmentVariable, Func<string> getToolsRoot)
    {
        _getEnvironmentVariable = getEnvironmentVariable;
        _getToolsRoot = getToolsRoot;
    }

    public FreeRoutingLocation Locate()
    {
        var exe = _getEnvironmentVariable("FREEROUTING_EXE");
        if (!string.IsNullOrWhiteSpace(exe))
        {
            var path = Path.GetFullPath(exe);
            return File.Exists(path)
                ? new FreeRoutingLocation(true, path, FreeRoutingExecutableType.Executable, "FREEROUTING_EXE", null)
                : new FreeRoutingLocation(false, null, FreeRoutingExecutableType.Unknown, "FREEROUTING_EXE", $"FREEROUTING_EXE points to a missing file: {path}");
        }

        var jar = _getEnvironmentVariable("FREEROUTING_JAR");
        if (!string.IsNullOrWhiteSpace(jar))
        {
            var path = Path.GetFullPath(jar);
            return File.Exists(path)
                ? new FreeRoutingLocation(true, path, FreeRoutingExecutableType.Jar, "FREEROUTING_JAR", null)
                : new FreeRoutingLocation(false, null, FreeRoutingExecutableType.Unknown, "FREEROUTING_JAR", $"FREEROUTING_JAR points to a missing file: {path}");
        }

        var cached = FindCachedFreeRouting();
        if (cached is not null)
        {
            var type = cached.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                ? FreeRoutingExecutableType.Jar
                : FreeRoutingExecutableType.Executable;
            return new FreeRoutingLocation(true, cached, type, "PCBHelper tools cache", null);
        }

        return new FreeRoutingLocation(false, null, FreeRoutingExecutableType.Unknown, "environment", "Set FREEROUTING_JAR or FREEROUTING_EXE to enable autorouting.");
    }

    public JavaLocation LocateJava()
    {
        foreach (var javaHome in GetEnvironmentValues("JAVA_HOME"))
        {
            var candidate = Path.Combine(javaHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
            if (File.Exists(candidate))
            {
                return new JavaLocation(true, Path.GetFullPath(candidate), "JAVA_HOME", null);
            }
        }

        foreach (var path in GetEnvironmentValues("PATH"))
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "java.exe" : "java");
                if (File.Exists(candidate))
                {
                    return new JavaLocation(true, Path.GetFullPath(candidate), "PATH", null);
                }
            }
        }

        return new JavaLocation(false, null, "environment", "Java was not found on PATH and JAVA_HOME is not set.");
    }

    public string GetToolsRoot()
    {
        return _getToolsRoot();
    }

    private string? FindCachedFreeRouting()
    {
        var root = _getToolsRoot();
        if (!Directory.Exists(root))
        {
            return null;
        }

        var candidates = Directory.GetFiles(root, "freerouting*.jar", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(root, OperatingSystem.IsWindows() ? "freerouting*.exe" : "freerouting*", SearchOption.AllDirectories))
            .Where(File.Exists)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName)
            .ToArray();

        return candidates.FirstOrDefault();
    }

    private static string GetDefaultToolsRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pcbhelper");
        }

        return Path.Combine(localAppData, "PCBHelper", "tools", "freerouting");
    }

    private IEnumerable<string> GetEnvironmentValues(string name)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in new[]
        {
            _getEnvironmentVariable(name),
            Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine)
        })
        {
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
            {
                yield return value;
            }
        }
    }
}

public sealed class FreeRoutingSetupService
{
    private readonly FreeRoutingLocator _locator;
    private readonly IFreeRoutingReleaseClient _releaseClient;

    public FreeRoutingSetupService()
        : this(new FreeRoutingLocator(), new GitHubFreeRoutingReleaseClient())
    {
    }

    public FreeRoutingSetupService(FreeRoutingLocator locator, IFreeRoutingReleaseClient releaseClient)
    {
        _locator = locator;
        _releaseClient = releaseClient;
    }

    public async Task<ToolResponse<FreeRoutingSetupResult>> SetupAsync(bool dryRun, CancellationToken cancellationToken = default)
    {
        var current = _locator.Locate();
        var java = _locator.LocateJava();
        if (current.Found)
        {
            return ToolResponse<FreeRoutingSetupResult>.Ok(
                "FreeRouting is already available.",
                new FreeRoutingSetupResult(dryRun, false, current, java, null, current.ExecutablePath, Array.Empty<string>()));
        }

        var release = await _releaseClient.GetLatestJarAsync(cancellationToken);
        if (!release.Success || release.Data is null)
        {
            return ToolResponse<FreeRoutingSetupResult>.Fail(release.Summary, release.Error?.Code ?? "FREEROUTING_RELEASE_LOOKUP_FAILED", release.Error?.Message);
        }

        var targetDirectory = Path.Combine(_locator.GetToolsRoot(), release.Data.TagName);
        var targetPath = Path.Combine(targetDirectory, release.Data.AssetName);
        var warnings = new List<string>();
        if (!java.Found)
        {
            warnings.Add("Java was not found. FreeRouting JAR setup can complete, but autorouting will still require Java 21+ on PATH or JAVA_HOME.");
        }

        if (dryRun)
        {
            return ToolResponse<FreeRoutingSetupResult>.Ok(
                $"FreeRouting setup would download {release.Data.AssetName}.",
                new FreeRoutingSetupResult(true, false, _locator.Locate(), java, release.Data, targetPath, Array.Empty<string>()),
                warnings);
        }

        Directory.CreateDirectory(targetDirectory);
        var tempPath = targetPath + ".download";
        await _releaseClient.DownloadAsync(release.Data.DownloadUrl, tempPath, cancellationToken);
        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        File.Move(tempPath, targetPath);
        var after = _locator.Locate();
        return ToolResponse<FreeRoutingSetupResult>.Ok(
            $"Downloaded FreeRouting to {targetPath}.",
            new FreeRoutingSetupResult(false, true, after, java, release.Data, targetPath, new[] { targetPath }),
            warnings);
    }
}

public interface IFreeRoutingReleaseClient
{
    Task<ToolResponse<FreeRoutingReleaseAsset>> GetLatestJarAsync(CancellationToken cancellationToken = default);

    Task DownloadAsync(Uri downloadUrl, string targetPath, CancellationToken cancellationToken = default);
}

public sealed class GitHubFreeRoutingReleaseClient : IFreeRoutingReleaseClient
{
    private static readonly Uri LatestReleaseUri = new("https://api.github.com/repos/freerouting/freerouting/releases/latest");
    private readonly HttpClient _client;

    public GitHubFreeRoutingReleaseClient()
        : this(new HttpClient())
    {
    }

    public GitHubFreeRoutingReleaseClient(HttpClient client)
    {
        _client = client;
        if (!_client.DefaultRequestHeaders.UserAgent.Any())
        {
            _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PCBHelper", "1.0"));
        }
    }

    public async Task<ToolResponse<FreeRoutingReleaseAsset>> GetLatestJarAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync(LatestReleaseUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return ToolResponse<FreeRoutingReleaseAsset>.Fail(
                $"Could not read FreeRouting latest release: HTTP {(int)response.StatusCode}.",
                "FREEROUTING_RELEASE_LOOKUP_FAILED",
                response.ReasonPhrase);
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? "latest" : "latest";
        if (!root.TryGetProperty("assets", out var assets))
        {
            return ToolResponse<FreeRoutingReleaseAsset>.Fail("FreeRouting latest release has no assets.", "FREEROUTING_RELEASE_ASSET_NOT_FOUND");
        }

        var jar = assets.EnumerateArray()
            .Select(static asset => new
            {
                Name = asset.TryGetProperty("name", out var name) ? name.GetString() : null,
                Url = asset.TryGetProperty("browser_download_url", out var url) ? url.GetString() : null
            })
            .Where(static asset => asset.Name is not null
                && asset.Url is not null
                && asset.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                && !asset.Name.Contains("sources", StringComparison.OrdinalIgnoreCase)
                && !asset.Name.Contains("javadoc", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static asset => asset.Name)
            .FirstOrDefault();

        if (jar is null)
        {
            return ToolResponse<FreeRoutingReleaseAsset>.Fail("FreeRouting latest release has no downloadable JAR asset.", "FREEROUTING_RELEASE_ASSET_NOT_FOUND");
        }

        return ToolResponse<FreeRoutingReleaseAsset>.Ok(
            $"Found FreeRouting release {tag}.",
            new FreeRoutingReleaseAsset(tag, jar.Name!, new Uri(jar.Url!)));
    }

    public async Task DownloadAsync(Uri downloadUrl, string targetPath, CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync(downloadUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(targetPath);
        await source.CopyToAsync(target, cancellationToken);
    }
}

public sealed record AutorouteBoardResult(
    bool DryRun,
    string BoardFile,
    string RoutingRoot,
    string DsnPath,
    string SesPath,
    string MetadataPath,
    AutoroutingBackendDiscovery Backend,
    IReadOnlyList<string> GeneratedFiles,
    string? Warning);

public sealed record AutoroutingCommand(string FileName, IReadOnlyList<string> Arguments);

public sealed record AutoroutingBackendDiscovery(
    KiCadCliLocation KiCadCli,
    FreeRoutingLocation FreeRouting,
    JavaLocation Java);

public sealed record FreeRoutingLocation(
    bool Found,
    string? ExecutablePath,
    FreeRoutingExecutableType ExecutableType,
    string Source,
    string? Message);

public sealed record JavaLocation(bool Found, string? ExecutablePath, string Source, string? Message);

public sealed record FreeRoutingReleaseAsset(
    string TagName,
    string AssetName,
    Uri DownloadUrl);

public sealed record FreeRoutingSetupResult(
    bool DryRun,
    bool Installed,
    FreeRoutingLocation FreeRouting,
    JavaLocation Java,
    FreeRoutingReleaseAsset? Release,
    string? TargetPath,
    IReadOnlyList<string> GeneratedFiles);

public enum FreeRoutingExecutableType
{
    Unknown,
    Jar,
    Executable
}
