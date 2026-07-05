using System.Globalization;

namespace PCBHelper.Core;

public sealed class ExportService
{
    private readonly ProjectDiscoveryService _projectDiscovery;
    private readonly KiCadCliLocator _locator;
    private readonly ICommandRunner _runner;

    public ExportService(ProjectDiscoveryService projectDiscovery, KiCadCliLocator locator, ICommandRunner runner)
    {
        _projectDiscovery = projectDiscovery;
        _locator = locator;
        _runner = runner;
    }

    public async Task<ToolResponse<ManufacturingExportResult>> ExportManufacturingFilesAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        var gerbers = await ExportGerbersAsync(projectPath, cancellationToken);
        var drill = await ExportDrillAsync(projectPath, cancellationToken);

        var exports = new List<SingleExportResult>();
        var warnings = new List<string>();
        if (gerbers.Data is not null)
        {
            exports.Add(gerbers.Data);
        }
        else if (gerbers.Error is not null)
        {
            warnings.Add(gerbers.Error.Message);
        }

        if (drill.Data is not null)
        {
            exports.Add(drill.Data);
        }
        else if (drill.Error is not null)
        {
            warnings.Add(drill.Error.Message);
        }

        if (exports.Count == 0)
        {
            return ToolResponse<ManufacturingExportResult>.Fail(
                "No manufacturing files were exported.",
                "EXPORT_FAILED",
                string.Join(Environment.NewLine, warnings));
        }

        var result = new ManufacturingExportResult(exports, exports.SelectMany(static export => export.GeneratedFiles).ToArray());
        return ToolResponse<ManufacturingExportResult>.Ok(
            $"Exported {result.GeneratedFiles.Count} manufacturing file(s).",
            result,
            warnings);
    }

    public Task<ToolResponse<SingleExportResult>> ExportGerbersAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        return RunExportAsync(projectPath, "gerbers", cancellationToken);
    }

    public Task<ToolResponse<SingleExportResult>> ExportDrillAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        return RunExportAsync(projectPath, "drill", cancellationToken);
    }

    public Task<ToolResponse<SingleExportResult>> ExportBomAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        return RunExportAsync(projectPath, "bom", cancellationToken);
    }

    public Task<ToolResponse<SingleExportResult>> ExportPositionFilesAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        return RunExportAsync(projectPath, "position", cancellationToken);
    }

    private async Task<ToolResponse<SingleExportResult>> RunExportAsync(
        string projectPath,
        string kind,
        CancellationToken cancellationToken)
    {
        var cli = _locator.Locate();
        if (!cli.Found || cli.ExecutablePath is null)
        {
            return ToolResponse<SingleExportResult>.Fail("kicad-cli was not found.", "KICAD_CLI_NOT_FOUND", cli.Message);
        }

        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<SingleExportResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        if (kind is "bom" && project.Data.SchematicFile is null)
        {
            return ToolResponse<SingleExportResult>.Fail(
                "BOM export requires a .kicad_sch file.",
                "SCHEMATIC_FILE_MISSING",
                $"Project root: {project.Data.ProjectRoot}");
        }

        if (kind is not "bom" && project.Data.BoardFile is null)
        {
            return ToolResponse<SingleExportResult>.Fail(
                $"{kind} export requires a .kicad_pcb file.",
                "BOARD_FILE_MISSING",
                $"Project root: {project.Data.ProjectRoot}");
        }

        var exportRoot = ExportPathFactory.CreateExportDirectory(project.Data.ProjectRoot);
        var outputDirectory = Path.Combine(exportRoot, kind);
        Directory.CreateDirectory(outputDirectory);

        var before = Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outputFile = kind switch
        {
            "bom" => Path.Combine(outputDirectory, $"{project.Data.ProjectName}-bom.csv"),
            "position" => Path.Combine(outputDirectory, $"{project.Data.ProjectName}-positions.csv"),
            _ => outputDirectory
        };
        var arguments = kind switch
        {
            "gerbers" => new[] { "pcb", "export", "gerbers", "--output", outputDirectory, project.Data.BoardFile! },
            "drill" => new[] { "pcb", "export", "drill", "--output", outputDirectory, project.Data.BoardFile! },
            "bom" => new[] { "sch", "export", "bom", "--output", outputFile, project.Data.SchematicFile! },
            "position" => new[] { "pcb", "export", "pos", "--format", "csv", "--units", "mm", "--output", outputFile, project.Data.BoardFile! },
            _ => throw new InvalidOperationException($"Unknown export kind: {kind}")
        };

        var execution = await _runner.RunAsync(cli.ExecutablePath, arguments, project.Data.ProjectRoot, cancellationToken);
        var stdoutPath = Path.Combine(outputDirectory, $"{kind}.stdout.txt");
        var stderrPath = Path.Combine(outputDirectory, $"{kind}.stderr.txt");
        await File.WriteAllTextAsync(stdoutPath, execution.StandardOutput, cancellationToken);
        await File.WriteAllTextAsync(stderrPath, execution.StandardError, cancellationToken);

        var generated = Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories)
            .Where(file => !before.Contains(file))
            .Where(static file => new FileInfo(file).Length > 0)
            .Where(static file => !file.EndsWith(".stdout.txt", StringComparison.OrdinalIgnoreCase))
            .Where(static file => !file.EndsWith(".stderr.txt", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var result = new SingleExportResult(kind, outputDirectory, generated, execution.ExitCode);
        var summary = $"{kind} export finished with exit code {execution.ExitCode}; generated {generated.Length} non-empty file(s).";
        return ToolResponse<SingleExportResult>.Ok(summary, result);
    }
}

public static class ExportPathFactory
{
    public static string CreateExportDirectory(string projectRoot)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
        var directory = Path.Combine(projectRoot, ".pcbhelper", "exports", timestamp);
        Directory.CreateDirectory(directory);
        return directory;
    }
}

public sealed record ManufacturingExportResult(
    IReadOnlyList<SingleExportResult> Exports,
    IReadOnlyList<string> GeneratedFiles);

public sealed record SingleExportResult(
    string Kind,
    string OutputDirectory,
    IReadOnlyList<string> GeneratedFiles,
    int ExitCode);
