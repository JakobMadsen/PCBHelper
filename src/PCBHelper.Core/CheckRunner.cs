using System.Globalization;

namespace PCBHelper.Core;

public sealed class CheckRunner
{
    private readonly ProjectDiscoveryService _projectDiscovery;
    private readonly KiCadCliLocator _locator;
    private readonly ICommandRunner _runner;

    public CheckRunner(ProjectDiscoveryService projectDiscovery, KiCadCliLocator locator, ICommandRunner runner)
    {
        _projectDiscovery = projectDiscovery;
        _locator = locator;
        _runner = runner;
    }

    public async Task<ToolResponse<CheckRunResult>> RunChecksAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var erc = await RunErcAsync(projectPath, cancellationToken);
        var drc = await RunDrcAsync(projectPath, cancellationToken);

        var results = new List<SingleCheckResult>();
        var warnings = new List<string>();

        if (erc.Data is not null)
        {
            results.Add(erc.Data);
        }
        else
        {
            warnings.AddRange(erc.Warnings);
        }

        if (drc.Data is not null)
        {
            results.Add(drc.Data);
        }
        else
        {
            warnings.AddRange(drc.Warnings);
        }

        var success = results.Count > 0 && results.All(static result => result.ExitCode == 0);
        var summary = results.Count == 0
            ? "No KiCad checks could be run."
            : $"Ran {results.Count} KiCad check(s); {results.Count(result => result.ExitCode == 0)} completed with exit code 0.";

        return ToolResponse<CheckRunResult>.Ok(summary, new CheckRunResult(results, warnings), warnings);
    }

    public Task<ToolResponse<SingleCheckResult>> RunErcAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        return RunSingleCheckAsync(projectPath, CheckKind.Erc, cancellationToken);
    }

    public Task<ToolResponse<SingleCheckResult>> RunDrcAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        return RunSingleCheckAsync(projectPath, CheckKind.Drc, cancellationToken);
    }

    private async Task<ToolResponse<SingleCheckResult>> RunSingleCheckAsync(
        string projectPath,
        CheckKind kind,
        CancellationToken cancellationToken)
    {
        var cli = _locator.Locate();
        if (!cli.Found || cli.ExecutablePath is null)
        {
            return ToolResponse<SingleCheckResult>.Fail("kicad-cli was not found.", "KICAD_CLI_NOT_FOUND", cli.Message);
        }

        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<SingleCheckResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        var inputFile = kind == CheckKind.Erc ? project.Data.SchematicFile : project.Data.BoardFile;
        if (inputFile is null)
        {
            var missing = kind == CheckKind.Erc ? ".kicad_sch" : ".kicad_pcb";
            return ToolResponse<SingleCheckResult>.Fail(
                $"{kind} cannot run because the project has no {missing} file.",
                "CHECK_INPUT_MISSING",
                $"Project root: {project.Data.ProjectRoot}");
        }

        var reportRoot = Path.Combine(
            project.Data.ProjectRoot,
            ".pcbhelper",
            "reports",
            DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(reportRoot);

        var reportPath = Path.Combine(reportRoot, kind == CheckKind.Erc ? "erc.json" : "drc.json");
        var stdoutPath = Path.Combine(reportRoot, kind == CheckKind.Erc ? "erc.stdout.txt" : "drc.stdout.txt");
        var stderrPath = Path.Combine(reportRoot, kind == CheckKind.Erc ? "erc.stderr.txt" : "drc.stderr.txt");

        var arguments = kind == CheckKind.Erc
            ? new[] { "sch", "erc", "--format", "json", "--output", reportPath, inputFile }
            : new[] { "pcb", "drc", "--format", "json", "--output", reportPath, inputFile };

        var execution = await _runner.RunAsync(cli.ExecutablePath, arguments, project.Data.ProjectRoot, cancellationToken);
        await File.WriteAllTextAsync(stdoutPath, execution.StandardOutput, cancellationToken);
        await File.WriteAllTextAsync(stderrPath, execution.StandardError, cancellationToken);

        var generatedFiles = new List<string> { stdoutPath, stderrPath };
        if (File.Exists(reportPath))
        {
            generatedFiles.Insert(0, reportPath);
        }

        var findingCount = File.Exists(reportPath)
            ? ReportFindingCounter.CountFindings(reportPath)
            : null;

        var result = new SingleCheckResult(
            kind.ToString().ToLowerInvariant(),
            inputFile,
            reportPath,
            stdoutPath,
            stderrPath,
            generatedFiles,
            execution.ExitCode,
            findingCount);

        var summary = $"{kind} finished with exit code {execution.ExitCode}. Report: {reportPath}";
        return ToolResponse<SingleCheckResult>.Ok(summary, result);
    }
}

public enum CheckKind
{
    Erc,
    Drc
}

public sealed record CheckRunResult(IReadOnlyList<SingleCheckResult> Checks, IReadOnlyList<string> Warnings);

public sealed record SingleCheckResult(
    string Kind,
    string InputFile,
    string ReportPath,
    string StdoutPath,
    string StderrPath,
    IReadOnlyList<string> GeneratedFiles,
    int ExitCode,
    int? FindingCount);
