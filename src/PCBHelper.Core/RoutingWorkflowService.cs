namespace PCBHelper.Core;

public sealed class RoutingWorkflowService
{
    private readonly RoutingService _routing;
    private readonly CheckRunner _checkRunner;
    private readonly ChangeReportService _changeReports;

    public RoutingWorkflowService(RoutingService routing, CheckRunner checkRunner, ChangeReportService changeReports)
    {
        _routing = routing;
        _checkRunner = checkRunner;
        _changeReports = changeReports;
    }

    public ToolResponse<TrackListResult> ListTracks(string projectPath, string? net = null)
    {
        return _routing.ListTracks(projectPath, net);
    }

    public ToolResponse<ViaListResult> ListVias(string projectPath, string? net = null)
    {
        return _routing.ListVias(projectPath, net);
    }

    public ToolResponse<NetRoutingResult> GetNetRouting(string projectPath, string net)
    {
        return _routing.GetNetRouting(projectPath, net);
    }

    public Task<ToolResponse<RoutingMutationResult>> AddTrackAsync(
        string projectPath,
        string net,
        double startXMillimeters,
        double startYMillimeters,
        double endXMillimeters,
        double endYMillimeters,
        string layer,
        double widthMillimeters,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var result = _routing.AddTrack(projectPath, net, startXMillimeters, startYMillimeters, endXMillimeters, endYMillimeters, layer, widthMillimeters, dryRun);
        return CompleteMutationAsync(projectPath, result, cancellationToken);
    }

    public Task<ToolResponse<RoutingMutationResult>> DeleteTrackAsync(
        string projectPath,
        string track,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var result = _routing.DeleteTrack(projectPath, track, dryRun);
        return CompleteMutationAsync(projectPath, result, cancellationToken);
    }

    public Task<ToolResponse<RoutingMutationResult>> AddViaAsync(
        string projectPath,
        string net,
        double xMillimeters,
        double yMillimeters,
        double sizeMillimeters,
        double drillMillimeters,
        string layers,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var result = _routing.AddVia(projectPath, net, xMillimeters, yMillimeters, sizeMillimeters, drillMillimeters, layers, dryRun);
        return CompleteMutationAsync(projectPath, result, cancellationToken);
    }

    public Task<ToolResponse<RoutingMutationResult>> DeleteViaAsync(
        string projectPath,
        string via,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var result = _routing.DeleteVia(projectPath, via, dryRun);
        return CompleteMutationAsync(projectPath, result, cancellationToken);
    }

    public async Task<ToolResponse<RoutingMutationResult>> RestoreRoutingChangeAsync(
        string projectPath,
        ChangeReport report,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var result = _routing.RestoreRoutingChange(projectPath, report, dryRun);
        if (dryRun || !result.Success || result.Data is null)
        {
            return result;
        }

        return await CompleteMutationAsync(projectPath, result, cancellationToken);
    }

    private async Task<ToolResponse<RoutingMutationResult>> CompleteMutationAsync(
        string projectPath,
        ToolResponse<RoutingMutationResult> mutation,
        CancellationToken cancellationToken)
    {
        if (!mutation.Success || mutation.Data is null || mutation.Data.DryRun)
        {
            return mutation;
        }

        var checks = await _checkRunner.RunDrcAsync(projectPath, cancellationToken);
        var report = await WriteRoutingReportAsync(projectPath, mutation.Data, checks, cancellationToken);
        if (!report.Success || report.Data is null)
        {
            return ToolResponse<RoutingMutationResult>.Fail(report.Summary, report.Error?.Code ?? "CHANGE_REPORT_FAILED", report.Error?.Message);
        }

        var result = mutation.Data with
        {
            ChangeReportPath = report.Data.ReportPath,
            CheckSummary = checks.Summary,
            CheckReportPaths = ChangeReportService.GetCheckReportPaths(checks.Data is null
                ? null
                : new CheckRunResult(new[] { checks.Data }, Array.Empty<string>()))
        };

        return ToolResponse<RoutingMutationResult>.Ok(
            $"{mutation.Summary} Change report: {report.Data.ReportPath}",
            result,
            checks.Warnings.Concat(report.Warnings).ToArray());
    }

    private Task<ToolResponse<ChangeReportWriteResult>> WriteRoutingReportAsync(
        string projectPath,
        RoutingMutationResult mutation,
        ToolResponse<SingleCheckResult> check,
        CancellationToken cancellationToken)
    {
        var checks = check.Data is null
            ? new CheckRunResult(Array.Empty<SingleCheckResult>(), check.Warnings)
            : new CheckRunResult(new[] { check.Data }, check.Warnings);
        var input = new ChangeReportInput(
            mutation.Operation,
            mutation.Item.Id,
            null,
            null,
            null,
            null,
            mutation.ChangedFile,
            new Placement(0, 0, null),
            new Placement(0, 0, null),
            check.Summary,
            ChangeReportService.GetCheckReportPaths(checks),
            RoutingItemKind: mutation.Item.Kind,
            RoutingItemId: mutation.Item.Id,
            RoutingItemBefore: mutation.Item.BeforeText,
            RoutingItemAfter: mutation.Item.AfterText);

        return _changeReports.WriteAsync(projectPath, input, cancellationToken);
    }
}
