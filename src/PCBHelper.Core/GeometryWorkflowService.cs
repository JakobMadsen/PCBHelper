namespace PCBHelper.Core;

public sealed class GeometryWorkflowService
{
    private readonly GeometryService _geometry;
    private readonly CheckRunner _checkRunner;
    private readonly ChangeReportService _changeReports;

    public GeometryWorkflowService(GeometryService geometry, CheckRunner checkRunner, ChangeReportService changeReports)
    {
        _geometry = geometry;
        _checkRunner = checkRunner;
        _changeReports = changeReports;
    }

    public async Task<ToolResponse<ComponentMutationResult>> MoveComponentAsync(
        string projectPath,
        string reference,
        double xMillimeters,
        double yMillimeters,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var move = _geometry.MoveComponent(projectPath, reference, xMillimeters, yMillimeters, dryRun);
        if (!move.Success || move.Data is null)
        {
            return ToolResponse<ComponentMutationResult>.Fail(move.Summary, move.Error?.Code ?? "MOVE_FAILED", move.Error?.Message);
        }

        if (dryRun)
        {
            return ToolResponse<ComponentMutationResult>.Ok(
                move.Summary,
                ComponentMutationResult.FromMove(move.Data, null, null, Array.Empty<string>()));
        }

        var checks = await _checkRunner.RunChecksAsync(projectPath, cancellationToken);
        var report = await WriteMoveReportAsync(projectPath, "move", move.Data, checks, null, null, null, cancellationToken);
        if (!report.Success || report.Data is null)
        {
            return ToolResponse<ComponentMutationResult>.Fail(report.Summary, report.Error?.Code ?? "CHANGE_REPORT_FAILED", report.Error?.Message);
        }

        var warnings = checks.Warnings.Concat(report.Warnings).ToArray();
        return ToolResponse<ComponentMutationResult>.Ok(
            $"{move.Summary} Change report: {report.Data.ReportPath}",
            ComponentMutationResult.FromMove(move.Data, report.Data.ReportPath, checks.Summary, report.Data.Report.CheckReportPaths),
            warnings);
    }

    public async Task<ToolResponse<ComponentSpacingMutationResult>> SetComponentSpacingAsync(
        string projectPath,
        string fixedReference,
        string movingReference,
        double distanceMillimeters,
        string? axis,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var spacing = _geometry.SetComponentSpacing(projectPath, fixedReference, movingReference, distanceMillimeters, axis, dryRun);
        if (!spacing.Success || spacing.Data is null)
        {
            return ToolResponse<ComponentSpacingMutationResult>.Fail(spacing.Summary, spacing.Error?.Code ?? "SPACING_FAILED", spacing.Error?.Message);
        }

        if (dryRun)
        {
            return ToolResponse<ComponentSpacingMutationResult>.Ok(
                spacing.Summary,
                ComponentSpacingMutationResult.FromSpacing(spacing.Data, null, null, Array.Empty<string>()));
        }

        var checks = await _checkRunner.RunChecksAsync(projectPath, cancellationToken);
        var move = new ComponentMoveResult(
            spacing.Data.MovingReference,
            spacing.Data.ChangedFile,
            spacing.Data.DryRun,
            spacing.Data.Before,
            spacing.Data.After);
        var report = await WriteMoveReportAsync(
            projectPath,
            "set-spacing",
            move,
            checks,
            spacing.Data.FixedReference,
            spacing.Data.Axis,
            distanceMillimeters,
            cancellationToken);
        if (!report.Success || report.Data is null)
        {
            return ToolResponse<ComponentSpacingMutationResult>.Fail(report.Summary, report.Error?.Code ?? "CHANGE_REPORT_FAILED", report.Error?.Message);
        }

        var warnings = checks.Warnings.Concat(report.Warnings).ToArray();
        return ToolResponse<ComponentSpacingMutationResult>.Ok(
            $"{spacing.Summary} Change report: {report.Data.ReportPath}",
            ComponentSpacingMutationResult.FromSpacing(spacing.Data, report.Data.ReportPath, checks.Summary, report.Data.Report.CheckReportPaths),
            warnings);
    }

    public async Task<ToolResponse<ComponentRestoreResult>> RestoreChangeAsync(
        string projectPath,
        string changeIdOrPath,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var report = _changeReports.Read(projectPath, changeIdOrPath);
        if (!report.Success || report.Data is null)
        {
            return ToolResponse<ComponentRestoreResult>.Fail(report.Summary, report.Error?.Code ?? "CHANGE_REPORT_NOT_FOUND", report.Error?.Message);
        }

        if (report.Data.Operation is not "move" and not "set-spacing" and not "restore-change")
        {
            return ToolResponse<ComponentRestoreResult>.Fail(
                $"Restore supports only single-footprint placement reports. Operation was: {report.Data.Operation}",
                "RESTORE_OPERATION_UNSUPPORTED");
        }

        var move = _geometry.MoveComponent(
            projectPath,
            report.Data.Reference,
            report.Data.Before.XMillimeters,
            report.Data.Before.YMillimeters,
            dryRun);
        if (!move.Success || move.Data is null)
        {
            return ToolResponse<ComponentRestoreResult>.Fail(move.Summary, move.Error?.Code ?? "RESTORE_FAILED", move.Error?.Message);
        }

        if (dryRun)
        {
            return ToolResponse<ComponentRestoreResult>.Ok(
                $"Previewed restore of {report.Data.Reference} from change {report.Data.ChangeId}.",
                ComponentRestoreResult.FromMove(report.Data.ChangeId, move.Data, null, null, Array.Empty<string>()));
        }

        var checks = await _checkRunner.RunChecksAsync(projectPath, cancellationToken);
        var restoreReport = await WriteMoveReportAsync(projectPath, "restore-change", move.Data, checks, null, null, null, cancellationToken);
        if (!restoreReport.Success || restoreReport.Data is null)
        {
            return ToolResponse<ComponentRestoreResult>.Fail(restoreReport.Summary, restoreReport.Error?.Code ?? "CHANGE_REPORT_FAILED", restoreReport.Error?.Message);
        }

        var warnings = checks.Warnings.Concat(restoreReport.Warnings).ToArray();
        return ToolResponse<ComponentRestoreResult>.Ok(
            $"Restored {report.Data.Reference} from change {report.Data.ChangeId}. Change report: {restoreReport.Data.ReportPath}",
            ComponentRestoreResult.FromMove(report.Data.ChangeId, move.Data, restoreReport.Data.ReportPath, checks.Summary, restoreReport.Data.Report.CheckReportPaths),
            warnings);
    }

    private Task<ToolResponse<ChangeReportWriteResult>> WriteMoveReportAsync(
        string projectPath,
        string operation,
        ComponentMoveResult move,
        ToolResponse<CheckRunResult> checks,
        string? fixedReference,
        string? axis,
        double? targetDistanceMillimeters,
        CancellationToken cancellationToken)
    {
        var input = new ChangeReportInput(
            operation,
            move.Reference,
            fixedReference,
            fixedReference is null ? null : move.Reference,
            axis,
            targetDistanceMillimeters,
            move.ChangedFile,
            move.Before,
            move.After,
            checks.Summary,
            ChangeReportService.GetCheckReportPaths(checks.Data));

        return _changeReports.WriteAsync(projectPath, input, cancellationToken);
    }
}

public sealed record ComponentMutationResult(
    string Reference,
    string ChangedFile,
    bool DryRun,
    Placement Before,
    Placement After,
    string? ChangeReportPath,
    string? CheckSummary,
    IReadOnlyList<string> CheckReportPaths)
{
    public static ComponentMutationResult FromMove(
        ComponentMoveResult move,
        string? changeReportPath,
        string? checkSummary,
        IReadOnlyList<string> checkReportPaths)
    {
        return new ComponentMutationResult(
            move.Reference,
            move.ChangedFile,
            move.DryRun,
            move.Before,
            move.After,
            changeReportPath,
            checkSummary,
            checkReportPaths);
    }
}

public sealed record ComponentSpacingMutationResult(
    string FixedReference,
    string MovingReference,
    string Axis,
    bool DryRun,
    double BeforeDistanceMillimeters,
    double AfterDistanceMillimeters,
    Placement Before,
    Placement After,
    string ChangedFile,
    string? ChangeReportPath,
    string? CheckSummary,
    IReadOnlyList<string> CheckReportPaths)
{
    public static ComponentSpacingMutationResult FromSpacing(
        ComponentSpacingResult spacing,
        string? changeReportPath,
        string? checkSummary,
        IReadOnlyList<string> checkReportPaths)
    {
        return new ComponentSpacingMutationResult(
            spacing.FixedReference,
            spacing.MovingReference,
            spacing.Axis,
            spacing.DryRun,
            spacing.BeforeDistanceMillimeters,
            spacing.AfterDistanceMillimeters,
            spacing.Before,
            spacing.After,
            spacing.ChangedFile,
            changeReportPath,
            checkSummary,
            checkReportPaths);
    }
}

public sealed record ComponentRestoreResult(
    string SourceChangeId,
    string Reference,
    string ChangedFile,
    bool DryRun,
    Placement Before,
    Placement After,
    string? ChangeReportPath,
    string? CheckSummary,
    IReadOnlyList<string> CheckReportPaths)
{
    public static ComponentRestoreResult FromMove(
        string sourceChangeId,
        ComponentMoveResult move,
        string? changeReportPath,
        string? checkSummary,
        IReadOnlyList<string> checkReportPaths)
    {
        return new ComponentRestoreResult(
            sourceChangeId,
            move.Reference,
            move.ChangedFile,
            move.DryRun,
            move.Before,
            move.After,
            changeReportPath,
            checkSummary,
            checkReportPaths);
    }
}
