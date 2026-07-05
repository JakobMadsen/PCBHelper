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
        var changeRoot = Path.Combine(project.Data.ProjectRoot, ".pcbhelper", "changes", changeId);
        Directory.CreateDirectory(changeRoot);

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
            input.ValueLocations);

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

            return ToolResponse<ChangeReport>.Ok($"Read change report: {path.Data}", report);
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

        if (File.Exists(changeIdOrPath))
        {
            return ToolResponse<string>.Ok("Resolved change report path.", Path.GetFullPath(changeIdOrPath));
        }

        if (changeIdOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || changeIdOrPath.Contains(Path.DirectorySeparatorChar)
            || changeIdOrPath.Contains(Path.AltDirectorySeparatorChar))
        {
            var fullPath = Path.GetFullPath(changeIdOrPath);
            return File.Exists(fullPath)
                ? ToolResponse<string>.Ok("Resolved change report path.", fullPath)
                : ToolResponse<string>.Fail($"Change report was not found: {fullPath}", "CHANGE_REPORT_NOT_FOUND");
        }

        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<string>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        var candidate = Path.Combine(project.Data.ProjectRoot, ".pcbhelper", "changes", changeIdOrPath, "change.json");
        return File.Exists(candidate)
            ? ToolResponse<string>.Ok("Resolved change report path.", candidate)
            : ToolResponse<string>.Fail($"Change report was not found: {candidate}", "CHANGE_REPORT_NOT_FOUND");
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
    IReadOnlyList<ChangeValueLocation>? ValueLocations = null);

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
    IReadOnlyList<ChangeValueLocation>? ValueLocations = null);

public sealed record ChangeReportWriteResult(string ChangeId, string ReportPath, ChangeReport Report);

public sealed record ChangeValueLocation(
    string Source,
    string File,
    string Reference,
    string BeforeValue,
    string AfterValue);
