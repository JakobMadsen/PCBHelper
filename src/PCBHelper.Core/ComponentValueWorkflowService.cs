namespace PCBHelper.Core;

public sealed class ComponentValueWorkflowService
{
    private readonly ComponentService _components;
    private readonly CheckRunner _checkRunner;
    private readonly ChangeReportService _changeReports;

    public ComponentValueWorkflowService(ComponentService components, CheckRunner checkRunner, ChangeReportService changeReports)
    {
        _components = components;
        _checkRunner = checkRunner;
        _changeReports = changeReports;
    }

    public ToolResponse<ComponentListResult> ListComponents(string projectPath)
    {
        return _components.ListComponents(projectPath);
    }

    public ToolResponse<ComponentValueResult> GetValue(string projectPath, string reference)
    {
        return _components.GetValue(projectPath, reference);
    }

    public async Task<ToolResponse<ComponentValueMutationResult>> SetValueAsync(
        string projectPath,
        string reference,
        string value,
        string? scope,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var mutation = _components.SetValue(projectPath, reference, value, scope, dryRun);
        if (!mutation.Success || mutation.Data is null)
        {
            return mutation;
        }

        if (dryRun)
        {
            return mutation;
        }

        var checks = await _checkRunner.RunChecksAsync(projectPath, cancellationToken);
        var report = await WriteValueReportAsync(projectPath, "set-value", mutation.Data, checks, cancellationToken);
        if (!report.Success || report.Data is null)
        {
            return ToolResponse<ComponentValueMutationResult>.Fail(report.Summary, report.Error?.Code ?? "CHANGE_REPORT_FAILED", report.Error?.Message);
        }

        var result = mutation.Data with
        {
            ChangeReportPath = report.Data.ReportPath,
            CheckSummary = checks.Summary,
            CheckReportPaths = report.Data.Report.CheckReportPaths
        };
        return ToolResponse<ComponentValueMutationResult>.Ok(
            $"{mutation.Summary} Change report: {report.Data.ReportPath}",
            result,
            checks.Warnings.Concat(report.Warnings).ToArray());
    }

    internal async Task<ToolResponse<ComponentValueMutationResult>> RestoreValueAsync(
        string projectPath,
        ChangeReport report,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        if (report.ValueBefore is null)
        {
            return ToolResponse<ComponentValueMutationResult>.Fail("Change report does not include value restore data.", "VALUE_RESTORE_DATA_MISSING");
        }

        var mutation = _components.SetValue(projectPath, report.Reference, report.ValueBefore, "available", dryRun);
        if (!mutation.Success || mutation.Data is null)
        {
            return mutation;
        }

        if (dryRun)
        {
            return mutation;
        }

        var checks = await _checkRunner.RunChecksAsync(projectPath, cancellationToken);
        var restoreReport = await WriteValueReportAsync(projectPath, "restore-change", mutation.Data, checks, cancellationToken);
        if (!restoreReport.Success || restoreReport.Data is null)
        {
            return ToolResponse<ComponentValueMutationResult>.Fail(restoreReport.Summary, restoreReport.Error?.Code ?? "CHANGE_REPORT_FAILED", restoreReport.Error?.Message);
        }

        return ToolResponse<ComponentValueMutationResult>.Ok(
            $"Restored {report.Reference} value from change {report.ChangeId}. Change report: {restoreReport.Data.ReportPath}",
            mutation.Data with
            {
                ChangeReportPath = restoreReport.Data.ReportPath,
                CheckSummary = checks.Summary,
                CheckReportPaths = restoreReport.Data.Report.CheckReportPaths
            },
            checks.Warnings.Concat(restoreReport.Warnings).ToArray());
    }

    private Task<ToolResponse<ChangeReportWriteResult>> WriteValueReportAsync(
        string projectPath,
        string operation,
        ComponentValueMutationResult mutation,
        ToolResponse<CheckRunResult> checks,
        CancellationToken cancellationToken)
    {
        var firstBefore = mutation.Before.FirstOrDefault();
        var firstAfter = mutation.After.FirstOrDefault();
        var locations = mutation.Before.Zip(mutation.After)
            .Select(pair => new ChangeValueLocation(pair.First.Source, pair.First.File, pair.First.Reference, pair.First.Value, pair.Second.Value))
            .ToArray();

        var input = new ChangeReportInput(
            operation,
            mutation.Reference,
            null,
            null,
            null,
            null,
            mutation.ChangedFiles.FirstOrDefault() ?? string.Empty,
            new Placement(0, 0, null),
            new Placement(0, 0, null),
            checks.Summary,
            ChangeReportService.GetCheckReportPaths(checks.Data),
            firstBefore?.Value,
            firstAfter?.Value,
            mutation.ChangedFiles,
            locations);

        return _changeReports.WriteAsync(projectPath, input, cancellationToken);
    }
}
