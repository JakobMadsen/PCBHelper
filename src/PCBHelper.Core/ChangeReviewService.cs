namespace PCBHelper.Core;

public sealed class ChangeReviewService
{
    private readonly ProjectDiscoveryService _projectDiscovery;
    private readonly ChangeReportService _changeReports;
    private readonly GeometryWorkflowService _geometryWorkflow;
    private readonly ComponentValueWorkflowService _valueWorkflow;
    private readonly RoutingWorkflowService? _routingWorkflow;
    private readonly SchematicAuthoringWorkflowService? _schematicWorkflow;

    public ChangeReviewService(
        ProjectDiscoveryService projectDiscovery,
        ChangeReportService changeReports,
        GeometryWorkflowService geometryWorkflow,
        ComponentValueWorkflowService valueWorkflow,
        RoutingWorkflowService? routingWorkflow = null,
        SchematicAuthoringWorkflowService? schematicWorkflow = null)
    {
        _projectDiscovery = projectDiscovery;
        _changeReports = changeReports;
        _geometryWorkflow = geometryWorkflow;
        _valueWorkflow = valueWorkflow;
        _routingWorkflow = routingWorkflow;
        _schematicWorkflow = schematicWorkflow;
    }

    public ToolResponse<ChangeListResult> ListChanges(string projectPath)
    {
        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<ChangeListResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        var root = Path.Combine(project.Data.ProjectRoot, ".pcbhelper", "changes");
        if (!Directory.Exists(root))
        {
            return ToolResponse<ChangeListResult>.Ok("No change reports found.", new ChangeListResult(Array.Empty<ChangeListItem>()));
        }

        var items = Directory.GetFiles(root, "change.json", SearchOption.AllDirectories)
            .Select(path =>
            {
                var report = _changeReports.Read(projectPath, path);
                return report.Data is null
                    ? null
                    : new ChangeListItem(report.Data.ChangeId, report.Data.Operation, report.Data.Reference, report.Data.CreatedAtUtc, path, report.Data.RestoreCommand);
            })
            .Where(static item => item is not null)
            .Cast<ChangeListItem>()
            .OrderByDescending(static item => item.CreatedAtUtc)
            .ToArray();

        return ToolResponse<ChangeListResult>.Ok($"Found {items.Length} change report(s).", new ChangeListResult(items));
    }

    public ToolResponse<ChangeReport> GetChange(string projectPath, string change)
    {
        return _changeReports.Read(projectPath, change);
    }

    public async Task<ToolResponse<object>> RestoreChangeAsync(
        string projectPath,
        string change,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var report = _changeReports.Read(projectPath, change);
        if (!report.Success || report.Data is null)
        {
            return ToolResponse<object>.Fail(report.Summary, report.Error?.Code ?? "CHANGE_REPORT_NOT_FOUND", report.Error?.Message);
        }

        if (report.Data.Operation == "set-value"
            || (report.Data.Operation == "restore-change" && report.Data.ValueBefore is not null))
        {
            var restored = await _valueWorkflow.RestoreValueAsync(projectPath, report.Data, dryRun, cancellationToken);
            return restored.Success && restored.Data is not null
                ? ToolResponse<object>.Ok(restored.Summary, restored.Data, restored.Warnings)
                : ToolResponse<object>.Fail(restored.Summary, restored.Error?.Code ?? "RESTORE_FAILED", restored.Error?.Message);
        }

        if (report.Data.Operation is "add-track" or "delete-track" or "add-via" or "delete-via"
            || (report.Data.Operation == "restore-change" && report.Data.RoutingItemKind is not null))
        {
            if (_routingWorkflow is null)
            {
                return ToolResponse<object>.Fail("Routing restore is not configured.", "ROUTING_RESTORE_UNAVAILABLE");
            }

            var restored = await _routingWorkflow.RestoreRoutingChangeAsync(projectPath, report.Data, dryRun, cancellationToken);
            return restored.Success && restored.Data is not null
                ? ToolResponse<object>.Ok(restored.Summary, restored.Data, restored.Warnings)
                : ToolResponse<object>.Fail(restored.Summary, restored.Error?.Code ?? "RESTORE_FAILED", restored.Error?.Message);
        }

        if (report.Data.FileSnapshots is not null && report.Data.FileSnapshots.Count > 0)
        {
            if (_schematicWorkflow is null)
            {
                return ToolResponse<object>.Fail("File snapshot restore is not configured.", "FILE_RESTORE_UNAVAILABLE");
            }

            var restored = await _schematicWorkflow.RestoreFileSnapshotsAsync(report.Data, dryRun, cancellationToken);
            return restored.Success && restored.Data is not null
                ? ToolResponse<object>.Ok(restored.Summary, restored.Data, restored.Warnings)
                : ToolResponse<object>.Fail(restored.Summary, restored.Error?.Code ?? "RESTORE_FAILED", restored.Error?.Message);
        }

        var placement = await _geometryWorkflow.RestoreChangeAsync(projectPath, change, dryRun, cancellationToken);
        return placement.Success && placement.Data is not null
            ? ToolResponse<object>.Ok(placement.Summary, placement.Data, placement.Warnings)
            : ToolResponse<object>.Fail(placement.Summary, placement.Error?.Code ?? "RESTORE_FAILED", placement.Error?.Message);
    }
}

public sealed record ChangeListResult(IReadOnlyList<ChangeListItem> Changes);

public sealed record ChangeListItem(
    string ChangeId,
    string Operation,
    string Reference,
    DateTimeOffset CreatedAtUtc,
    string ReportPath,
    string RestoreCommand);
