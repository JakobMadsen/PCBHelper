using System.Globalization;
using System.Text.Json;

namespace PCBHelper.Core;

public sealed class ChangeReportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ProjectDiscoveryService _projectDiscovery;
    private readonly Func<DateTimeOffset> _utcNow;

    public ChangeReportService(ProjectDiscoveryService projectDiscovery)
        : this(projectDiscovery, () => DateTimeOffset.UtcNow)
    {
    }

    public ChangeReportService(ProjectDiscoveryService projectDiscovery, Func<DateTimeOffset> utcNow)
    {
        _projectDiscovery = projectDiscovery;
        _utcNow = utcNow;
    }

    public async Task<ToolResponse<ChangeReportWriteResult>> WriteAsync(
        string projectPath,
        ChangeReportInput input,
        CancellationToken cancellationToken = default)
    {
        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<ChangeReportWriteResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        var createdAt = _utcNow();
        var changeId = CreateChangeId(createdAt, input.Operation, input.Reference);
        var restoreCommand = $"pcbhelper restore-change \"{project.Data.ProjectRoot}\" --change \"{changeId}\"";
        var report = new ChangeReport(
            changeId,
            input.Operation,
            input.Reference,
            input.FixedReference,
            input.MovingReference,
            input.Axis,
            input.TargetDistanceMillimeters,
            input.ChangedFile,
            createdAt,
            input.Before,
            input.After,
            input.CheckSummary,
            input.CheckReportPaths,
            restoreCommand,
            input.ValueBefore,
            input.ValueAfter,
            input.ChangedFiles,
            input.ValueLocations,
            input.RoutingItemKind,
            input.RoutingItemId,
            input.RoutingItemBefore,
            input.RoutingItemAfter,
            input.FileSnapshots);
        var normalized = NormalizeReportPaths(projectPath, report);
        if (!normalized.Success || normalized.Data is null)
        {
            return ToolResponse<ChangeReportWriteResult>.Fail(normalized.Summary, normalized.Error?.Code ?? "PROJECT_SCOPE_VIOLATION", normalized.Error?.Message);
        }

        report = normalized.Data;
        var changeRoot = Path.Combine(project.Data.ProjectRoot, ".pcbhelper", "changes", changeId);
        Directory.CreateDirectory(changeRoot);

        var reportPath = Path.Combine(changeRoot, "change.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions), cancellationToken);

        return ToolResponse<ChangeReportWriteResult>.Ok(
            $"Wrote change report: {reportPath}",
            new ChangeReportWriteResult(changeId, reportPath, report));
    }

    public ToolResponse<ChangeReport> Read(string projectPath, string changeIdOrPath)
    {
        var path = ResolveReportPath(projectPath, changeIdOrPath);
        if (!path.Success || path.Data is null)
        {
            return ToolResponse<ChangeReport>.Fail(path.Summary, path.Error?.Code ?? "CHANGE_REPORT_NOT_FOUND", path.Error?.Message);
        }

        try
        {
            var report = JsonSerializer.Deserialize<ChangeReport>(File.ReadAllText(path.Data), JsonOptions);
            if (report is null)
            {
                return ToolResponse<ChangeReport>.Fail($"Change report is empty: {path.Data}", "CHANGE_REPORT_INVALID");
            }

            var normalized = NormalizeReportPaths(projectPath, report);
            return normalized.Success && normalized.Data is not null
                ? ToolResponse<ChangeReport>.Ok($"Read change report: {path.Data}", normalized.Data)
                : ToolResponse<ChangeReport>.Fail(normalized.Summary, normalized.Error?.Code ?? "PROJECT_SCOPE_VIOLATION", normalized.Error?.Message);
        }
        catch (JsonException exception)
        {
            return ToolResponse<ChangeReport>.Fail($"Change report is invalid JSON: {path.Data}", "CHANGE_REPORT_INVALID", exception.Message);
        }
    }

    public ToolResponse<string> ResolveReportPath(string projectPath, string changeIdOrPath)
    {
        if (string.IsNullOrWhiteSpace(changeIdOrPath))
        {
            return ToolResponse<string>.Fail("restore-change requires --change.", "CHANGE_REQUIRED");
        }

        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<string>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        var changesRoot = Path.Combine(project.Data.ProjectRoot, ".pcbhelper", "changes");
        var hasPathSyntax = changeIdOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || changeIdOrPath.Contains(Path.DirectorySeparatorChar)
            || changeIdOrPath.Contains(Path.AltDirectorySeparatorChar)
            || Path.IsPathRooted(changeIdOrPath);
        var candidate = hasPathSyntax
            ? Path.GetFullPath(changeIdOrPath, project.Data.ProjectRoot)
            : Path.Combine(changesRoot, changeIdOrPath, "change.json");
        if (!ProjectScopePolicy.IsWithin(changesRoot, candidate))
        {
            return ToolResponse<string>.Fail(
                "Change reports must be read from the selected project's .pcbhelper/changes directory.",
                "PROJECT_SCOPE_VIOLATION");
        }

        return File.Exists(candidate)
            ? ToolResponse<string>.Ok("Resolved change report path.", candidate)
            : ToolResponse<string>.Fail($"Change report was not found: {candidate}", "CHANGE_REPORT_NOT_FOUND");
    }

    private ToolResponse<ChangeReport> NormalizeReportPaths(string projectPath, ChangeReport report)
    {
        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<ChangeReport>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        string? Normalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            var fullPath = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(path, project.Data.ProjectRoot);
            return ProjectScopePolicy.IsWithin(project.Data.ProjectRoot, fullPath) ? fullPath : null;
        }

        var changedFile = Normalize(report.ChangedFile);
        if (!string.IsNullOrWhiteSpace(report.ChangedFile) && changedFile is null)
        {
            return ToolResponse<ChangeReport>.Fail("Change report references a file outside the selected project.", "PROJECT_SCOPE_VIOLATION");
        }

        var changedFiles = new List<string>();
        foreach (var file in report.ChangedFiles ?? Array.Empty<string>())
        {
            var normalized = Normalize(file);
            if (normalized is null)
            {
                return ToolResponse<ChangeReport>.Fail("Change report references a file outside the selected project.", "PROJECT_SCOPE_VIOLATION");
            }

            changedFiles.Add(normalized);
        }

        var locations = new List<ChangeValueLocation>();
        foreach (var location in report.ValueLocations ?? Array.Empty<ChangeValueLocation>())
        {
            var normalized = Normalize(location.File);
            if (normalized is null)
            {
                return ToolResponse<ChangeReport>.Fail("Change report value location is outside the selected project.", "PROJECT_SCOPE_VIOLATION");
            }

            locations.Add(location with { File = normalized });
        }

        var snapshots = new List<ChangeFileSnapshot>();
        foreach (var snapshot in report.FileSnapshots ?? Array.Empty<ChangeFileSnapshot>())
        {
            var normalized = Normalize(snapshot.File);
            if (normalized is null)
            {
                return ToolResponse<ChangeReport>.Fail("Change report snapshot is outside the selected project.", "PROJECT_SCOPE_VIOLATION");
            }

            snapshots.Add(snapshot with { File = normalized });
        }

        return ToolResponse<ChangeReport>.Ok(
            "Validated change report paths.",
            report with
            {
                ChangedFile = changedFile ?? string.Empty,
                ChangedFiles = report.ChangedFiles is null ? null : changedFiles,
                ValueLocations = report.ValueLocations is null ? null : locations,
                FileSnapshots = report.FileSnapshots is null ? null : snapshots
            });
    }

    public static IReadOnlyList<string> GetCheckReportPaths(CheckRunResult? result)
    {
        return result?.Checks
            .SelectMany(static check => check.GeneratedFiles)
            .Where(File.Exists)
            .ToArray() ?? Array.Empty<string>();
    }

    private static string CreateChangeId(DateTimeOffset createdAt, string operation, string reference)
    {
        return string.Join(
            "-",
            createdAt.UtcDateTime.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture),
            Sanitize(operation),
            Sanitize(reference),
            Guid.NewGuid().ToString("N")[..8]);
    }

    private static string Sanitize(string value)
    {
        var chars = value
            .Select(static character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-')
            .ToArray();

        return new string(chars).Trim('-');
    }
}

public sealed record ChangeReportInput(
    string Operation,
    string Reference,
    string? FixedReference,
    string? MovingReference,
    string? Axis,
    double? TargetDistanceMillimeters,
    string ChangedFile,
    Placement Before,
    Placement After,
    string CheckSummary,
    IReadOnlyList<string> CheckReportPaths,
    string? ValueBefore = null,
    string? ValueAfter = null,
    IReadOnlyList<string>? ChangedFiles = null,
    IReadOnlyList<ChangeValueLocation>? ValueLocations = null,
    string? RoutingItemKind = null,
    string? RoutingItemId = null,
    string? RoutingItemBefore = null,
    string? RoutingItemAfter = null,
    IReadOnlyList<ChangeFileSnapshot>? FileSnapshots = null);

public sealed record ChangeReport(
    string ChangeId,
    string Operation,
    string Reference,
    string? FixedReference,
    string? MovingReference,
    string? Axis,
    double? TargetDistanceMillimeters,
    string ChangedFile,
    DateTimeOffset CreatedAtUtc,
    Placement Before,
    Placement After,
    string CheckSummary,
    IReadOnlyList<string> CheckReportPaths,
    string RestoreCommand,
    string? ValueBefore = null,
    string? ValueAfter = null,
    IReadOnlyList<string>? ChangedFiles = null,
    IReadOnlyList<ChangeValueLocation>? ValueLocations = null,
    string? RoutingItemKind = null,
    string? RoutingItemId = null,
    string? RoutingItemBefore = null,
    string? RoutingItemAfter = null,
    IReadOnlyList<ChangeFileSnapshot>? FileSnapshots = null);

public sealed record ChangeReportWriteResult(string ChangeId, string ReportPath, ChangeReport Report);

public sealed record ChangeValueLocation(
    string Source,
    string File,
    string Reference,
    string BeforeValue,
    string AfterValue);

public sealed record ChangeFileSnapshot(
    string File,
    string? BeforeText,
    string? AfterText);
