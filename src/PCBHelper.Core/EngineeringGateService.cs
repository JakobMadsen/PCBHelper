namespace PCBHelper.Core;

public sealed class EngineeringGateService
{
    private readonly CheckSummaryService _checkSummary;
    private readonly AssemblyService _assembly;
    private readonly SimulationService? _simulations;

    public EngineeringGateService(CheckSummaryService checkSummary, AssemblyService assembly, SimulationService? simulations = null)
    {
        _checkSummary = checkSummary;
        _assembly = assembly;
        _simulations = simulations;
    }

    public async Task<ToolResponse<EngineeringGateResult>> RunAsync(
        string projectPath,
        EngineeringGateRequirements? requirements = null,
        CancellationToken cancellationToken = default)
    {
        requirements ??= EngineeringGateRequirements.Default;
        var checks = new List<EngineeringGateCheck>();
        var checkSummary = await _checkSummary.RunAsync(projectPath, cancellationToken);
        AddKiCadCheck("erc", requirements.Erc, checkSummary, checks);
        AddKiCadCheck("drc", requirements.Drc, checkSummary, checks);

        if (!IsSkipped(requirements.ManufacturingValidation))
        {
            var validation = _assembly.ValidateAssemblyPackage(projectPath);
            checks.Add(validation.Data is null
                ? new EngineeringGateCheck(
                    "manufacturing-validation",
                    Requirement(requirements.ManufacturingValidation),
                    EngineeringGateCheckStatus.ExecutionFailed,
                    0,
                    validation.Error?.Message ?? validation.Summary,
                    Array.Empty<string>())
                : new EngineeringGateCheck(
                    "manufacturing-validation",
                    Requirement(requirements.ManufacturingValidation),
                    validation.Data.Valid ? EngineeringGateCheckStatus.Passed : EngineeringGateCheckStatus.FindingsPresent,
                    validation.Data.ErrorCount + validation.Data.WarningCount,
                    validation.Summary,
                    Array.Empty<string>()));
        }

        if (!IsSkipped(requirements.Simulation))
        {
            if (_simulations is null || !_simulations.GetCapabilities().Available)
                checks.Add(new EngineeringGateCheck("simulation", Requirement(requirements.Simulation), EngineeringGateCheckStatus.Unavailable, 0,
                    _simulations?.GetCapabilities().Message ?? "Simulation service is unavailable.", Array.Empty<string>()));
            else
            {
                var simulation = await _simulations.RunAsync(projectPath, cancellationToken: cancellationToken);
                var simulationStatus = simulation.Data is not null
                    ? simulation.Data.Passed ? EngineeringGateCheckStatus.Passed : EngineeringGateCheckStatus.FindingsPresent
                    : simulation.Error?.Code is "SIMULATOR_NOT_FOUND" or "SIMULATION_MODEL_UNAVAILABLE" or "SIMULATION_TESTS_NOT_FOUND"
                        ? EngineeringGateCheckStatus.Unavailable : EngineeringGateCheckStatus.ExecutionFailed;
                checks.Add(new EngineeringGateCheck("simulation", Requirement(requirements.Simulation), simulationStatus,
                    simulation.Data?.Tests.Sum(static test => test.Assertions.Count(static assertion => !assertion.Passed)) ?? 0,
                    simulation.Summary, simulation.Data is null ? Array.Empty<string>() : new[] { simulation.Data.OutputDirectory }));
            }
        }

        var required = checks.Where(static check => check.Required).ToArray();
        var status = required.Any(static check => check.Status == EngineeringGateCheckStatus.ExecutionFailed)
            ? EngineeringGateStatus.ExecutionFailed
            : required.Any(static check => check.Status == EngineeringGateCheckStatus.Unavailable)
                ? EngineeringGateStatus.Unavailable
                : required.Any(static check => check.Status == EngineeringGateCheckStatus.FindingsPresent)
                    ? EngineeringGateStatus.FindingsPresent
                    : EngineeringGateStatus.Passed;
        var result = new EngineeringGateResult(status, checks, DateTimeOffset.UtcNow);
        return ToolResponse<EngineeringGateResult>.Ok(
            status == EngineeringGateStatus.Passed
                ? "Engineering gate passed."
                : $"Engineering gate status: {ToWireName(status)}.",
            result,
            checkSummary.Warnings);
    }

    private static void AddKiCadCheck(
        string kind,
        string requirement,
        ToolResponse<CheckSummaryResult> summary,
        ICollection<EngineeringGateCheck> target)
    {
        if (IsSkipped(requirement))
        {
            return;
        }

        var raw = summary.Data?.RawChecks.Checks.FirstOrDefault(check => check.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));
        if (raw is null)
        {
            target.Add(new EngineeringGateCheck(
                kind,
                Requirement(requirement),
                summary.Error is null ? EngineeringGateCheckStatus.Unavailable : EngineeringGateCheckStatus.ExecutionFailed,
                0,
                summary.Error?.Message ?? $"{kind.ToUpperInvariant()} was unavailable.",
                Array.Empty<string>()));
            return;
        }

        var findings = summary.Data!.Findings.Count(finding => finding.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));
        var status = raw.ExitCode == 0 && findings == 0
            ? EngineeringGateCheckStatus.Passed
            : File.Exists(raw.ReportPath)
                ? EngineeringGateCheckStatus.FindingsPresent
                : EngineeringGateCheckStatus.ExecutionFailed;
        target.Add(new EngineeringGateCheck(
            kind,
            Requirement(requirement),
            status,
            findings,
            $"{kind.ToUpperInvariant()} exited with {raw.ExitCode} and reported {findings} finding(s).",
            raw.GeneratedFiles));
    }

    private static bool IsSkipped(string value)
    {
        return value.Equals("skip", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Requirement(string value)
    {
        return value.Equals("required", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsValidRequirement(string? value)
    {
        return value is not null && (value.Equals("required", StringComparison.OrdinalIgnoreCase)
            || value.Equals("optional", StringComparison.OrdinalIgnoreCase)
            || value.Equals("skip", StringComparison.OrdinalIgnoreCase));
    }

    private static string ToWireName(EngineeringGateStatus status)
    {
        return status switch
        {
            EngineeringGateStatus.Passed => "passed",
            EngineeringGateStatus.FindingsPresent => "findings-present",
            EngineeringGateStatus.Unavailable => "unavailable",
            EngineeringGateStatus.ExecutionFailed => "execution-failed",
            _ => status.ToString().ToLowerInvariant()
        };
    }
}

public sealed record EngineeringGateRequirements(
    string Erc = "required",
    string Drc = "required",
    string ManufacturingValidation = "required",
    string Simulation = "skip")
{
    public static EngineeringGateRequirements Default { get; } = new();
}

public enum EngineeringGateStatus
{
    Passed,
    FindingsPresent,
    Unavailable,
    ExecutionFailed
}

public enum EngineeringGateCheckStatus
{
    Passed,
    FindingsPresent,
    Unavailable,
    ExecutionFailed
}

public sealed record EngineeringGateResult(
    EngineeringGateStatus Status,
    IReadOnlyList<EngineeringGateCheck> Checks,
    DateTimeOffset RanAtUtc);

public sealed record EngineeringGateCheck(
    string Kind,
    bool Required,
    EngineeringGateCheckStatus Status,
    int FindingCount,
    string Summary,
    IReadOnlyList<string> ReportPaths);
