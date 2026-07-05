using System.Text.Json;

namespace PCBHelper.Core;

public sealed class CheckSummaryService
{
    private readonly CheckRunner _checkRunner;

    public CheckSummaryService(CheckRunner checkRunner)
    {
        _checkRunner = checkRunner;
    }

    public async Task<ToolResponse<CheckSummaryResult>> RunAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var checks = await _checkRunner.RunChecksAsync(projectPath, cancellationToken);
        if (checks.Data is null)
        {
            return ToolResponse<CheckSummaryResult>.Fail(checks.Summary, checks.Error?.Code ?? "CHECK_FAILED", checks.Error?.Message);
        }

        var findings = checks.Data.Checks.SelectMany(ExtractFindings).ToArray();
        var result = new CheckSummaryResult(checks.Data, findings);
        return ToolResponse<CheckSummaryResult>.Ok($"Found {findings.Length} check finding(s).", result, checks.Warnings);
    }

    private static IReadOnlyList<CheckFinding> ExtractFindings(SingleCheckResult check)
    {
        if (!File.Exists(check.ReportPath))
        {
            return Array.Empty<CheckFinding>();
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(check.ReportPath));
            var findings = new List<CheckFinding>();
            Walk(check.Kind, document.RootElement, findings);
            return findings;
        }
        catch (JsonException)
        {
            return Array.Empty<CheckFinding>();
        }
    }

    private static void Walk(string kind, JsonElement element, List<CheckFinding> findings)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var message = TryString(element, "description") ?? TryString(element, "message") ?? TryString(element, "text");
            var severity = TryString(element, "severity") ?? TryString(element, "type");
            if (message is not null)
            {
                findings.Add(new CheckFinding(kind, severity, message));
            }

            foreach (var property in element.EnumerateObject())
            {
                Walk(kind, property.Value, findings);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                Walk(kind, item, findings);
            }
        }
    }

    private static string? TryString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}

public sealed record CheckSummaryResult(CheckRunResult RawChecks, IReadOnlyList<CheckFinding> Findings);

public sealed record CheckFinding(string Kind, string? Severity, string Message);
