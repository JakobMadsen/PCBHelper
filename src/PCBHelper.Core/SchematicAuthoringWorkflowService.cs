namespace PCBHelper.Core;

public sealed class SchematicAuthoringWorkflowService
{
    private readonly SchematicAuthoringService _schematic;
    private readonly CheckRunner _checkRunner;
    private readonly ChangeReportService _changeReports;

    public SchematicAuthoringWorkflowService(SchematicAuthoringService schematic, CheckRunner checkRunner, ChangeReportService changeReports)
    {
        _schematic = schematic;
        _checkRunner = checkRunner;
        _changeReports = changeReports;
    }

    public ToolResponse<SchematicSymbolListResult> ListSymbols(string projectPath)
    {
        return _schematic.ListSymbols(projectPath);
    }

    public Task<ToolResponse<SchematicMutationResult>> CreateSymbolAsync(string projectPath, string symbol, string reference, double x, double y, string? value, string? footprint, bool dryRun, CancellationToken cancellationToken = default)
    {
        return CompleteMutationAsync(projectPath, _schematic.CreateSymbol(projectPath, symbol, reference, x, y, value, footprint, dryRun), cancellationToken);
    }

    public Task<ToolResponse<SchematicMutationResult>> SetSymbolFieldAsync(string projectPath, string reference, string field, string value, bool dryRun, CancellationToken cancellationToken = default)
    {
        return CompleteMutationAsync(projectPath, _schematic.SetSymbolField(projectPath, reference, field, value, dryRun), cancellationToken);
    }

    public Task<ToolResponse<SchematicMutationResult>> ConnectPinsAsync(string projectPath, string from, string to, string? net, bool dryRun, CancellationToken cancellationToken = default)
    {
        return CompleteMutationAsync(projectPath, _schematic.ConnectPins(projectPath, from, to, net, dryRun), cancellationToken);
    }

    public Task<ToolResponse<SchematicMutationResult>> AddNetLabelAsync(string projectPath, string net, double x, double y, bool dryRun, CancellationToken cancellationToken = default)
    {
        return CompleteMutationAsync(projectPath, _schematic.AddNetLabel(projectPath, net, x, y, dryRun), cancellationToken);
    }

    public Task<ToolResponse<SchematicMutationResult>> UpdatePcbFromSchematicAsync(string projectPath, bool dryRun, CancellationToken cancellationToken = default)
    {
        return CompleteMutationAsync(projectPath, _schematic.UpdatePcbFromSchematic(projectPath, dryRun), cancellationToken);
    }

    public Task<ToolResponse<SchematicMutationResult>> RestoreFileSnapshotsAsync(ChangeReport report, bool dryRun, CancellationToken cancellationToken = default)
    {
        return CompleteMutationAsync(string.Empty, _schematic.RestoreFileSnapshots(report, dryRun), cancellationToken, writeReport: false);
    }

    private async Task<ToolResponse<SchematicMutationResult>> CompleteMutationAsync(
        string projectPath,
        ToolResponse<SchematicMutationResult> mutation,
        CancellationToken cancellationToken,
        bool writeReport = true)
    {
        if (!mutation.Success || mutation.Data is null || mutation.Data.DryRun)
        {
            return mutation;
        }

        ToolResponse<CheckRunResult>? checks = null;
        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            checks = await _checkRunner.RunChecksAsync(projectPath, cancellationToken);
        }

        if (!writeReport)
        {
            return mutation;
        }

        var report = await _changeReports.WriteAsync(
            projectPath,
            new ChangeReportInput(
                mutation.Data.Operation,
                mutation.Data.Reference,
                null,
                null,
                null,
                null,
                mutation.Data.FileSnapshots.FirstOrDefault()?.File ?? string.Empty,
                new Placement(0, 0, null),
                new Placement(0, 0, null),
                checks?.Summary ?? "Checks not run.",
                ChangeReportService.GetCheckReportPaths(checks?.Data),
                ChangedFiles: mutation.Data.FileSnapshots.Select(static snapshot => snapshot.File).ToArray(),
                FileSnapshots: mutation.Data.FileSnapshots),
            cancellationToken);
        if (!report.Success || report.Data is null)
        {
            return ToolResponse<SchematicMutationResult>.Fail(report.Summary, report.Error?.Code ?? "CHANGE_REPORT_FAILED", report.Error?.Message);
        }

        return ToolResponse<SchematicMutationResult>.Ok(
            $"{mutation.Summary} Change report: {report.Data.ReportPath}",
            mutation.Data with
            {
                ChangeReportPath = report.Data.ReportPath,
                CheckReportPaths = report.Data.Report.CheckReportPaths
            },
            checks?.Warnings.Concat(report.Warnings).ToArray() ?? report.Warnings);
    }
}
