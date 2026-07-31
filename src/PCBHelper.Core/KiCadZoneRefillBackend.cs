using System.Diagnostics;
using System.Text;

namespace PCBHelper.Core;

public interface IKiCadZoneRefillBackend
{
    Task<ZoneRefillBackendResult> RefillAsync(
        string boardPath,
        string evidenceDirectory,
        CancellationToken cancellationToken);
}

public sealed class KiCadPythonZoneRefillBackend : IKiCadZoneRefillBackend
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly KiCadCliLocator _locator;
    private readonly ICommandRunner _runner;
    private readonly TimeSpan _timeout;

    public KiCadPythonZoneRefillBackend(
        KiCadCliLocator locator,
        ICommandRunner runner,
        TimeSpan? timeout = null)
    {
        _locator = locator;
        _runner = runner;
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<ZoneRefillBackendResult> RefillAsync(
        string boardPath,
        string evidenceDirectory,
        CancellationToken cancellationToken)
    {
        var cli = _locator.Locate();
        var pythonPath = cli.ExecutablePath is null ? null : ResolvePythonPath(cli.ExecutablePath);
        if (pythonPath is null)
        {
            return new ZoneRefillBackendResult(
                false,
                -1,
                string.Empty,
                cli.Message ?? "KiCad's bundled Python executable was not found beside kicad-cli.",
                null);
        }

        Directory.CreateDirectory(evidenceDirectory);
        var scriptPath = Path.Combine(evidenceDirectory, "zone-refill.py");
        await File.WriteAllTextAsync(scriptPath, PythonScript, new UTF8Encoding(false), cancellationToken);

        CommandExecutionResult execution;
        try
        {
            execution = OperatingSystem.IsWindows()
                ? await RunWindowsAsync(pythonPath, scriptPath, boardPath, evidenceDirectory, cancellationToken)
                : await RunRedirectedAsync(pythonPath, scriptPath, boardPath, evidenceDirectory, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            execution = new CommandExecutionResult(
                -1,
                string.Empty,
                $"KiCad Python zone refill could not start: {exception.Message}");
        }

        await File.WriteAllTextAsync(
            Path.Combine(evidenceDirectory, "stdout.txt"),
            execution.StandardOutput,
            CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(evidenceDirectory, "stderr.txt"),
            execution.StandardError,
            CancellationToken.None);

        return new ZoneRefillBackendResult(
            execution.ExitCode == 0,
            execution.ExitCode,
            execution.StandardOutput,
            execution.StandardError,
            pythonPath);
    }

    private async Task<CommandExecutionResult> RunRedirectedAsync(
        string pythonPath,
        string scriptPath,
        string boardPath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            return await _runner.RunAsync(
                pythonPath,
                new[] { scriptPath, boardPath },
                workingDirectory,
                timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TimedOut();
        }
    }

    private async Task<CommandExecutionResult> RunWindowsAsync(
        string pythonPath,
        string scriptPath,
        string boardPath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var wrapperPath = Path.Combine(workingDirectory, "run-zone-refill.cmd");
        var stdoutPath = Path.Combine(workingDirectory, "run-zone-refill.stdout.log");
        var stderrPath = Path.Combine(workingDirectory, "run-zone-refill.stderr.log");
        await File.WriteAllTextAsync(
            wrapperPath,
            $"@echo off{Environment.NewLine}\"{pythonPath}\" \"{scriptPath}\" \"{boardPath}\" >\"{stdoutPath}\" 2>\"{stderrPath}\"{Environment.NewLine}",
            cancellationToken);

        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
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

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start KiCad Python zone refill.");
        var wait = process.WaitForExitAsync(cancellationToken);
        var completed = await Task.WhenAny(wait, Task.Delay(_timeout, CancellationToken.None));
        if (completed != wait)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            return TimedOut();
        }

        await wait;
        var stdout = File.Exists(stdoutPath) ? await File.ReadAllTextAsync(stdoutPath, CancellationToken.None) : string.Empty;
        var stderr = File.Exists(stderrPath) ? await File.ReadAllTextAsync(stderrPath, CancellationToken.None) : string.Empty;
        return new CommandExecutionResult(process.ExitCode, stdout, stderr);
    }

    private CommandExecutionResult TimedOut() => new(
        -1,
        string.Empty,
        $"KiCad Python zone refill timed out after {_timeout.TotalSeconds:0} seconds.");

    internal static string? ResolvePythonPath(string kiCadCliPath)
    {
        var configured = Environment.GetEnvironmentVariable("KICAD_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var directory = Path.GetDirectoryName(kiCadCliPath);
        if (string.IsNullOrWhiteSpace(directory)) return null;
        foreach (var name in OperatingSystem.IsWindows()
                     ? new[] { "python.exe", "pythonw.exe" }
                     : new[] { "python3", "python" })
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private const string PythonScript = """
import json
import os
import pcbnew
import sys

path = sys.argv[1]
board = pcbnew.LoadBoard(path)
zones = board.Zones()
filled = pcbnew.ZONE_FILLER(board).Fill(zones)
saved = pcbnew.SaveBoard(path, board) if filled else False
print(json.dumps({
    "kicadVersion": pcbnew.GetBuildVersion(),
    "zoneCount": len(zones),
    "filled": bool(filled),
    "saved": bool(saved)
}))
sys.stdout.flush()
sys.stderr.flush()
os._exit(0 if filled and saved else 2)
""";
}

public sealed record ZoneRefillBackendResult(
    bool Success,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string? PythonPath);
