using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCBHelper.Core;

public sealed class ReleaseAuditService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly ProjectDiscoveryService _projects;
    private readonly ComponentService _components;
    private readonly BoardInspectionService _inspection;
    private readonly RoutingService _routing;
    private readonly TestSpecService _tests;
    private readonly BestPracticeReviewService _bestPractices;
    private readonly Func<SimulationCapabilities> _simulationCapabilities;
    private readonly Func<DateTimeOffset> _clock;

    public ReleaseAuditService(
        ProjectDiscoveryService projects,
        ComponentService components,
        BoardInspectionService inspection,
        RoutingService routing,
        TestSpecService tests,
        BestPracticeReviewService bestPractices,
        Func<SimulationCapabilities> simulationCapabilities,
        Func<DateTimeOffset>? clock = null)
    {
        _projects = projects;
        _components = components;
        _inspection = inspection;
        _routing = routing;
        _tests = tests;
        _bestPractices = bestPractices;
        _simulationCapabilities = simulationCapabilities;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public ToolResponse<ReleaseAuditResult> Audit(
        string projectPath,
        string? policyPath = null,
        string? outputDirectory = null)
    {
        var project = _projects.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<ReleaseAuditResult>.Fail(
                project.Summary,
                project.Error?.Code ?? "PROJECT_NOT_FOUND",
                project.Error?.Message);
        }

        var root = project.Data.ProjectRoot;
        var resolvedPolicyPath = ResolvePolicyPath(root, policyPath);
        var authorizedPolicy = _projects.AuthorizePath(resolvedPolicyPath);
        if (!authorizedPolicy.Success || authorizedPolicy.Data is null)
        {
            return ToolResponse<ReleaseAuditResult>.Fail(
                authorizedPolicy.Summary,
                authorizedPolicy.Error?.Code ?? "PROJECT_SCOPE_VIOLATION",
                authorizedPolicy.Error?.Message);
        }
        resolvedPolicyPath = authorizedPolicy.Data;
        var policy = ReadPolicy(resolvedPolicyPath);
        if (!policy.Success || policy.Data is null)
        {
            return ToolResponse<ReleaseAuditResult>.Fail(
                policy.Summary,
                policy.Error?.Code ?? "RELEASE_AUDIT_POLICY_INVALID",
                policy.Error?.Message);
        }

        var validationError = ValidatePolicy(policy.Data);
        if (validationError is not null)
        {
            return ToolResponse<ReleaseAuditResult>.Fail(
                validationError,
                "RELEASE_AUDIT_POLICY_INVALID",
                validationError);
        }

        try
        {
            var context = new AuditContext(
                project.Data,
                resolvedPolicyPath,
                policy.Data,
                _projects,
                _components,
                _inspection,
                _routing,
                _tests,
                _bestPractices,
                _simulationCapabilities);

            RunChecks(context);

            var failCount = context.Checks.Count(static item => item.Status == ReleaseAuditCheckStatus.Fail);
            var warningCount = context.Checks.Count(static item => item.Status == ReleaseAuditCheckStatus.Warn);
            var disposition = failCount > 0
                ? ReleaseAuditDispositions.Blocked
                : warningCount > 0
                    ? ReleaseAuditDispositions.PrototypeOnly
                    : ReleaseAuditDispositions.Ready;

            var generatedAt = _clock();
            var output = ResolveOutputDirectory(root, outputDirectory, generatedAt);
            var authorizedOutput = _projects.AuthorizePath(output);
            if (!authorizedOutput.Success || authorizedOutput.Data is null)
            {
                return ToolResponse<ReleaseAuditResult>.Fail(
                    authorizedOutput.Summary,
                    authorizedOutput.Error?.Code ?? "PROJECT_SCOPE_VIOLATION",
                    authorizedOutput.Error?.Message);
            }
            output = authorizedOutput.Data;
            Directory.CreateDirectory(output);
            var jsonPath = Path.Combine(output, "release-audit.json");
            var markdownPath = Path.Combine(output, "release-audit.md");
            var result = new ReleaseAuditResult(
                1,
                generatedAt,
                root,
                resolvedPolicyPath,
                policy.Data.Name,
                disposition,
                new ReleaseAuditSummary(
                    context.Checks.Count(static item => item.Status == ReleaseAuditCheckStatus.Pass),
                    failCount,
                    warningCount),
                context.SourceFiles,
                context.Checks,
                jsonPath,
                markdownPath);

            File.WriteAllText(jsonPath, JsonSerializer.Serialize(result, JsonOptions) + Environment.NewLine);
            File.WriteAllText(markdownPath, RenderMarkdown(result));

            return ToolResponse<ReleaseAuditResult>.Ok(
                disposition == ReleaseAuditDispositions.Blocked
                    ? $"Release audit blocked with {failCount} failed check(s)."
                    : $"Release audit disposition: {disposition}.",
                result);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return ToolResponse<ReleaseAuditResult>.Fail(
                "Release audit could not be completed.",
                "RELEASE_AUDIT_EXECUTION_FAILED",
                exception.Message);
        }
    }

    private static string ResolvePolicyPath(string projectRoot, string? policyPath)
    {
        if (string.IsNullOrWhiteSpace(policyPath))
        {
            return Path.Combine(projectRoot, ".pcbhelper", "release-policy.json");
        }

        return Path.GetFullPath(policyPath);
    }

    private static string ResolveOutputDirectory(string projectRoot, string? outputDirectory, DateTimeOffset generatedAt)
    {
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            return Path.GetFullPath(outputDirectory);
        }

        return Path.Combine(
            projectRoot,
            ".pcbhelper",
            "release-audits",
            generatedAt.UtcDateTime.ToString("yyyyMMddTHHmmssfffZ"));
    }

    private static ToolResponse<ReleaseAuditPolicy> ReadPolicy(string path)
    {
        if (!File.Exists(path))
        {
            return ToolResponse<ReleaseAuditPolicy>.Fail(
                $"Release audit policy was not found: {path}",
                "RELEASE_AUDIT_POLICY_NOT_FOUND");
        }

        try
        {
            var policy = JsonSerializer.Deserialize<ReleaseAuditPolicy>(File.ReadAllText(path), JsonOptions);
            return policy is null
                ? ToolResponse<ReleaseAuditPolicy>.Fail(
                    $"Release audit policy is empty: {path}",
                    "RELEASE_AUDIT_POLICY_INVALID")
                : ToolResponse<ReleaseAuditPolicy>.Ok($"Read release audit policy: {path}", policy);
        }
        catch (JsonException exception)
        {
            return ToolResponse<ReleaseAuditPolicy>.Fail(
                $"Release audit policy is invalid JSON: {path}",
                "RELEASE_AUDIT_POLICY_INVALID",
                exception.Message);
        }
    }

    private static string? ValidatePolicy(ReleaseAuditPolicy policy)
    {
        if (policy.Version != 1)
        {
            return $"Unsupported release audit policy version: {policy.Version}.";
        }

        if (policy.Git is null
            || policy.ReleaseEvidence is null
            || policy.BestPracticeReview is null
            || policy.Simulation is null
            || policy.Intent is null
            || policy.ManualEvidence is null
            || policy.PinNetAssertions is null
            || policy.NetSetAssertions is null
            || policy.DcFeedbackAssertions is null
            || policy.MirroredValuePairs is null
            || policy.ReferenceGroups is null)
        {
            return "Release audit policy collections and sections cannot be null.";
        }

        if (policy.PinNetAssertions.Any(static item => item is null)
            || policy.NetSetAssertions.Any(static item => item is null)
            || policy.DcFeedbackAssertions.Any(static item => item is null)
            || policy.ReferenceGroups.Any(static item => item is null))
        {
            return "Release audit assertion and reference-group entries cannot be null.";
        }

        if (policy.ReleaseEvidence.RequiredChecks is null
            || policy.ReleaseEvidence.BlockingAssemblyDiagnosticCodes is null
            || policy.Intent.RequiredVoltageLimits is null
            || policy.ManualEvidence.RequiredItems is null)
        {
            return "Release audit nested collections cannot be null.";
        }

        var ids = policy.PinNetAssertions.Select(static item => item.Id)
            .Concat(policy.NetSetAssertions.Select(static item => item.Id))
            .Concat(policy.DcFeedbackAssertions.Select(static item => item.Id))
            .Concat(policy.ReferenceGroups.Select(static item => item.Id))
            .ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace))
        {
            return "Every release audit assertion and reference group requires an id.";
        }

        var duplicate = ids.GroupBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            return $"Duplicate release audit check id: {duplicate.Key}.";
        }

        if (policy.Simulation.MinimumTests < 0)
        {
            return "simulation.minimumTests must be non-negative.";
        }

        if (policy.MirroredValuePairs.Any(static pair =>
            pair is null || pair.Count != 2 || pair.Any(string.IsNullOrWhiteSpace)))
        {
            return "Each mirroredValuePairs entry must contain exactly two non-empty references.";
        }

        if (policy.PinNetAssertions.Any(static item =>
                item is null
                || string.IsNullOrWhiteSpace(item.Reference)
                || string.IsNullOrWhiteSpace(item.Pad)
                || string.IsNullOrWhiteSpace(item.Net))
            || policy.NetSetAssertions.Any(static item =>
                item is null
                || string.IsNullOrWhiteSpace(item.Reference)
                || item.Nets is null
                || item.Nets.Count == 0
                || item.Nets.Any(string.IsNullOrWhiteSpace))
            || policy.DcFeedbackAssertions.Any(static item =>
                item is null
                || string.IsNullOrWhiteSpace(item.FromNet)
                || string.IsNullOrWhiteSpace(item.ToNet)))
        {
            return "Release audit assertions require non-empty references, pads, and nets.";
        }

        if (policy.ReferenceGroups.Any(static group =>
            group is null
            || string.IsNullOrWhiteSpace(group.ReferenceProject)
            || group.NetMap is null
            || group.Components is null
            || group.Components.Count == 0))
        {
            return "Each reference group requires a referenceProject and at least one component mapping.";
        }

        if (string.IsNullOrWhiteSpace(policy.ManualEvidence.Path))
        {
            return "manualEvidence.path cannot be empty.";
        }

        return null;
    }

    private static void RunChecks(AuditContext context)
    {
        CheckProjectFiles(context);
        CheckGit(context);
        CheckReleaseEvidence(context);
        CheckBestPracticeReview(context);
        CheckSimulation(context);
        CheckUnrouted(context);
        CheckComponentConsistency(context);
        CheckIntent(context);
        CheckPinAssertions(context);
        CheckNetSets(context);
        CheckDcFeedback(context);
        CheckMirrors(context);
        CheckReferenceGroups(context);
        CheckManualEvidence(context);
    }

    private static void CheckProjectFiles(AuditContext context)
    {
        var root = context.Project.ProjectRoot;
        var sources = Directory.GetFiles(root, "*.kicad_sch", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(root, "*.kicad_pcb", SearchOption.TopDirectoryOnly))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var schematics = sources.Count(static path => path.EndsWith(".kicad_sch", StringComparison.OrdinalIgnoreCase));
        var boards = sources.Count(static path => path.EndsWith(".kicad_pcb", StringComparison.OrdinalIgnoreCase));
        foreach (var source in sources)
        {
            using var stream = File.OpenRead(source);
            context.SourceFiles.Add(new ReleaseAuditSourceFile(
                source,
                Convert.ToHexStringLower(SHA256.HashData(stream)),
                File.GetLastWriteTimeUtc(source)));
        }

        context.Add(
            "project-files",
            "project",
            schematics > 0 && boards > 0 ? ReleaseAuditCheckStatus.Pass : ReleaseAuditCheckStatus.Fail,
            schematics > 0 && boards > 0
                ? $"Found {schematics} schematic and {boards} board file(s)."
                : "A release requires both a KiCad schematic and board.",
            new { schematics, boards });
    }

    private static void CheckGit(AuditContext context)
    {
        if (!context.Policy.Git.Required)
        {
            return;
        }

        var inside = RunGit(context.Project.ProjectRoot, "rev-parse", "--is-inside-work-tree");
        if (inside.ExitCode != 0 || !inside.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            context.Add(
                "git-worktree",
                "configuration-control",
                ReleaseAuditCheckStatus.Fail,
                "Project is not in a Git worktree; released source cannot be traced.");
            return;
        }

        var head = RunGit(context.Project.ProjectRoot, "rev-parse", "HEAD");
        context.Add(
            "git-head",
            "configuration-control",
            head.ExitCode == 0 ? ReleaseAuditCheckStatus.Pass : ReleaseAuditCheckStatus.Fail,
            head.ExitCode == 0
                ? $"Release source resolves to commit {head.StandardOutput.Trim()}."
                : "Git worktree has no committed HEAD.");

        if (context.Policy.Git.RequireClean)
        {
            var status = RunGit(context.Project.ProjectRoot, "status", "--porcelain=v1");
            var dirty = status.StandardOutput.Split(
                new[] { "\r\n", "\n" },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            context.Add(
                "git-clean",
                "configuration-control",
                status.ExitCode == 0 && dirty.Length == 0
                    ? ReleaseAuditCheckStatus.Pass
                    : ReleaseAuditCheckStatus.Fail,
                status.ExitCode != 0
                    ? "Git status could not be read."
                    : dirty.Length == 0
                        ? "Git worktree is clean."
                        : $"Git worktree has {dirty.Length} uncommitted path(s).",
                dirty.Take(50).ToArray());
        }
    }

    private static GitResult RunGit(string workingDirectory, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // The project path has already passed PCBHelper's path authorization.
            // Git 2.35+ can nevertheless reject a repository owned by the desktop
            // user when PCBHelper runs in an isolated worker account. Scope the
            // exception to this child process and exact project path; never mutate
            // the user's global Git configuration.
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(
                $"safe.directory={Path.GetFullPath(workingDirectory).Replace('\\', '/')}");
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return new GitResult(-1, string.Empty, "Could not start git.");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new GitResult(process.ExitCode, stdout, stderr);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new GitResult(-1, string.Empty, exception.Message);
        }
    }

    private static void CheckReleaseEvidence(AuditContext context)
    {
        var policy = context.Policy.ReleaseEvidence;
        if (!policy.Required)
        {
            return;
        }

        var releases = Path.Combine(context.Project.ProjectRoot, ".pcbhelper", "releases");
        var reviews = Directory.Exists(releases)
            ? Directory.GetFiles(releases, "release-review.json", SearchOption.AllDirectories)
            : Array.Empty<string>();
        var reviewPath = reviews.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (reviewPath is null)
        {
            context.Add(
                "release-review",
                "release-evidence",
                ReleaseAuditCheckStatus.Fail,
                "No PCBHelper release-review.json was found.");
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(reviewPath));
        var root = document.RootElement;
        context.Add(
            "release-review",
            "release-evidence",
            ReleaseAuditCheckStatus.Pass,
            $"Latest release review: {reviewPath}.");

        var checks = TryGet(root, "engineeringGate", out var engineeringGate)
            && TryGet(engineeringGate, "checks", out var checkArray)
            && checkArray.ValueKind == JsonValueKind.Array
                ? checkArray.EnumerateArray().ToArray()
                : Array.Empty<JsonElement>();
        foreach (var kind in policy.RequiredChecks)
        {
            var expectedKind = kind.Equals("simulation-assertions", StringComparison.OrdinalIgnoreCase)
                ? "simulation"
                : kind;
            var match = checks.FirstOrDefault(item =>
                ReadString(item, "kind")?.Equals(expectedKind, StringComparison.OrdinalIgnoreCase) == true);
            var present = match.ValueKind != JsonValueKind.Undefined;
            var required = present && ReadBoolean(match, "required");
            var passed = present && PassedStatus(match, "status");
            if (expectedKind.Equals("drc", StringComparison.OrdinalIgnoreCase))
            {
                var findingCount = present
                    && TryGet(match, "findingCount", out var findingCountElement)
                    && findingCountElement.ValueKind == JsonValueKind.Number
                    && findingCountElement.TryGetInt32(out var parsedFindingCount)
                        ? parsedFindingCount
                        : (int?)null;
                context.ZoneAwareDrcConnectivityPassed = required && passed && findingCount == 0;
                context.LatestDrcFindingCount = findingCount;
            }

            context.Add(
                $"release-check-{kind}",
                "release-evidence",
                required && passed ? ReleaseAuditCheckStatus.Pass : ReleaseAuditCheckStatus.Fail,
                required && passed
                    ? $"Required release check '{kind}' passed."
                    : $"Required passing release check '{kind}' is missing.",
                present ? JsonSerializer.Deserialize<object>(match.GetRawText(), JsonOptions) : null);
        }

        if (policy.RequireFresh)
        {
            var newestSource = context.SourceFiles.OrderByDescending(static item => item.ModifiedUtc).FirstOrDefault();
            var reviewModified = File.GetLastWriteTimeUtc(reviewPath);
            var fresh = newestSource is not null && reviewModified >= newestSource.ModifiedUtc.UtcDateTime;
            context.Add(
                "release-evidence-freshness",
                "release-evidence",
                fresh ? ReleaseAuditCheckStatus.Pass : ReleaseAuditCheckStatus.Fail,
                fresh
                    ? "Release evidence is at least as new as the design sources."
                    : "Release evidence predates the current design sources.",
                new
                {
                    reviewModifiedUtc = reviewModified,
                    newestSource = newestSource?.Path,
                    sourceModifiedUtc = newestSource?.ModifiedUtc
                });
        }

        if (policy.BlockingAssemblyDiagnosticCodes.Count > 0)
        {
            var blockingCodes = policy.BlockingAssemblyDiagnosticCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var blocking = new List<object>();
            if (TryGet(root, "assembly", out var assembly)
                && TryGet(assembly, "diagnostics", out var diagnostics)
                && diagnostics.ValueKind == JsonValueKind.Array)
            {
                foreach (var diagnostic in diagnostics.EnumerateArray())
                {
                    if (ReadString(diagnostic, "code") is { } code && blockingCodes.Contains(code))
                    {
                        blocking.Add(JsonSerializer.Deserialize<object>(diagnostic.GetRawText(), JsonOptions)!);
                    }
                }
            }

            context.Add(
                "assembly-blocking-diagnostics",
                "manufacturing",
                blocking.Count == 0 ? ReleaseAuditCheckStatus.Pass : ReleaseAuditCheckStatus.Fail,
                blocking.Count == 0
                    ? "No policy-blocking assembly diagnostics remain."
                    : $"Found {blocking.Count} unresolved blocking assembly diagnostic(s).",
                blocking);
        }
    }

    private static bool PassedStatus(JsonElement item, string property)
    {
        if (!TryGet(item, property, out var status))
        {
            return false;
        }

        return status.ValueKind switch
        {
            JsonValueKind.Number => status.TryGetInt32(out var numeric) && numeric == 0,
            JsonValueKind.String => status.GetString() is { } value
                && (value.Equals("pass", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("passed", StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    private static void CheckBestPracticeReview(AuditContext context)
    {
        var policy = context.Policy.BestPracticeReview;
        if (!policy.Required)
        {
            return;
        }

        var validation = context.BestPractices.ValidateCurrent(context.Project.ProjectRoot);
        if (!validation.Success || validation.Data is null)
        {
            context.Add(
                "best-practice-review",
                "best-practice",
                ReleaseAuditCheckStatus.Fail,
                "A required best-practice review is missing or invalid.",
                new
                {
                    validation.Summary,
                    errorCode = validation.Error?.Code,
                    errorMessage = validation.Error?.Message
                });
            return;
        }

        var dispositionAccepted =
            validation.Data.Disposition == BestPracticeDisposition.Pass
            || policy.AllowPassWithConcerns
                && validation.Data.Disposition == BestPracticeDisposition.PassWithConcerns;
        var freshnessAccepted = !policy.RequireFresh || validation.Data.EvidenceCurrent;
        var passed = dispositionAccepted && freshnessAccepted;
        context.Add(
            "best-practice-review",
            "best-practice",
            passed ? ReleaseAuditCheckStatus.Pass : ReleaseAuditCheckStatus.Fail,
            passed
                ? $"Best-practice review {validation.Data.RunId} is accepted with disposition {validation.Data.Disposition}."
                : !freshnessAccepted
                    ? $"Best-practice review {validation.Data.RunId} is stale."
                    : $"Best-practice review {validation.Data.RunId} has blocking disposition {validation.Data.Disposition}.",
            validation.Data);
    }

    private static void CheckSimulation(AuditContext context)
    {
        if (!context.Policy.Simulation.Required)
        {
            return;
        }

        var capability = context.SimulationCapabilities();
        context.Add(
            "simulation-capability",
            "simulation",
            capability.Available ? ReleaseAuditCheckStatus.Pass : ReleaseAuditCheckStatus.Fail,
            capability.Available
                ? $"Simulation backend available: {capability.Backend}."
                : "No simulation backend is available.",
            capability);

        var tests = context.Tests.ListTests(context.Project.ProjectRoot);
        var simulationTests = context.Tests.LoadSimulationTests(context.Project.ProjectRoot, null);
        var count = simulationTests.Data?.Tests.Count ?? 0;
        context.Add(
            "simulation-test-count",
            "simulation",
            tests.Success && simulationTests.Success && count >= context.Policy.Simulation.MinimumTests
                ? ReleaseAuditCheckStatus.Pass
                : ReleaseAuditCheckStatus.Fail,
            $"Found {count} project simulation test(s); policy requires {context.Policy.Simulation.MinimumTests}.",
            tests.Data);
    }

    private static void CheckUnrouted(AuditContext context)
    {
        var unrouted = context.Routing.ListUnroutedConnections(context.Project.ProjectRoot);
        var count = unrouted.Data?.Nets.Count ?? 0;
        var passed = unrouted.Success && (count == 0 || context.ZoneAwareDrcConnectivityPassed);
        context.Add(
            "unrouted-connections",
            "layout",
            passed ? ReleaseAuditCheckStatus.Pass : ReleaseAuditCheckStatus.Fail,
            !unrouted.Success
                ? "Unrouted connectivity could not be inspected."
                : count == 0
                ? "No unrouted connections were reported."
                : context.ZoneAwareDrcConnectivityPassed
                    ? $"KiCad DRC reports 0 findings, including unconnected items; {count} track-only net record(s) are satisfied by zone-aware connectivity evidence."
                    : $"Found {count} unrouted net record(s).",
            new
            {
                trackOnlyNetRecords = unrouted.Data?.Nets,
                zoneAwareDrcPassed = context.ZoneAwareDrcConnectivityPassed,
                drcFindingCount = context.LatestDrcFindingCount
            });
    }

    private static void CheckComponentConsistency(AuditContext context)
    {
        var components = context.GetComponents(context.Project.ProjectRoot);
        if (components is null)
        {
            context.Add(
                "component-value-consistency",
                "netlist",
                ReleaseAuditCheckStatus.Fail,
                "Component values could not be inspected.");
            return;
        }

        var mismatches = components.Values
            .Where(static item => item.BoardValue is not null
                && item.SchematicValue is not null
                && !item.BoardValue.Equals(item.SchematicValue, StringComparison.Ordinal))
            .Select(static item => new
            {
                item.Reference,
                item.BoardValue,
                item.SchematicValue
            })
            .ToArray();
        context.Add(
            "component-value-consistency",
            "netlist",
            mismatches.Length == 0 ? ReleaseAuditCheckStatus.Pass : ReleaseAuditCheckStatus.Fail,
            mismatches.Length == 0
                ? "Board and schematic component values agree."
                : $"Found {mismatches.Length} board/schematic value mismatch(es).",
            mismatches);
    }

    private static void CheckIntent(AuditContext context)
    {
        var required = context.Policy.Intent.RequiredVoltageLimits;
        if (required.Count == 0)
        {
            return;
        }

        var path = Path.Combine(context.Project.ProjectRoot, ".pcbhelper", "design-intent.json");
        if (!File.Exists(path))
        {
            context.Add(
                "design-intent-voltage-limits",
                "design-intent",
                ReleaseAuditCheckStatus.Fail,
                "Design intent file is missing.");
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var signals = TryGet(document.RootElement, "signals", out var signalArray)
            && signalArray.ValueKind == JsonValueKind.Array
                ? signalArray.EnumerateArray()
                    .Where(static signal => ReadString(signal, "net") is not null)
                    .ToDictionary(
                        static signal => ReadString(signal, "net")!,
                        static signal => signal,
                        StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var missing = required.Where(net =>
        {
            if (!signals.TryGetValue(net, out var signal))
            {
                return true;
            }

            return !TryGet(signal, "minVoltage", out var minimum)
                || minimum.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                || !TryGet(signal, "maxVoltage", out var maximum)
                || maximum.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
        }).ToArray();

        context.Add(
            "design-intent-voltage-limits",
            "design-intent",
            missing.Length == 0 ? ReleaseAuditCheckStatus.Pass : ReleaseAuditCheckStatus.Fail,
            missing.Length == 0
                ? "All required analogue nets have min/max operating limits."
                : $"Missing min/max operating limits for: {string.Join(", ", missing)}.",
            new { required, missing });
    }

    private static void CheckPinAssertions(AuditContext context)
    {
        foreach (var assertion in context.Policy.PinNetAssertions)
        {
            var pads = context.GetPads(context.Project.ProjectRoot, assertion.Reference);
            var pad = pads?.FirstOrDefault(item => item.Name.Equals(assertion.Pad, StringComparison.OrdinalIgnoreCase));
            var actual = pad?.NetName;
            var passed = actual?.Equals(assertion.Net, StringComparison.OrdinalIgnoreCase) == true;
            context.Add(
                assertion.Id,
                "pin-net",
                passed ? ReleaseAuditCheckStatus.Pass : ReleaseAuditCheckStatus.Fail,
                passed
                    ? $"{assertion.Reference} pad {assertion.Pad} is on {assertion.Net}."
                    : $"{assertion.Reference} pad {assertion.Pad} is on '{actual}'; expected '{assertion.Net}'.",
                new
                {
                    assertion.Reference,
                    assertion.Pad,
                    actual,
                    expected = assertion.Net
                });
        }
    }

    private static void CheckNetSets(AuditContext context)
    {
        foreach (var assertion in context.Policy.NetSetAssertions)
        {
            var expected = assertion.Nets.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var actual = context.GetPads(context.Project.ProjectRoot, assertion.Reference)?
                .Select(static pad => pad.NetName ?? string.Empty)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>();
            var passed = actual.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase);
            context.Add(
                assertion.Id,
                "pin-net",
                passed ? ReleaseAuditCheckStatus.Pass : ReleaseAuditCheckStatus.Fail,
                passed
                    ? $"{assertion.Reference} connects exactly [{string.Join(", ", expected)}]."
                    : $"{assertion.Reference} connects [{string.Join(", ", actual)}]; expected [{string.Join(", ", expected)}].",
                new { assertion.Reference, actual, expected });
        }
    }

    private static void CheckDcFeedback(AuditContext context)
    {
        if (context.Policy.DcFeedbackAssertions.Count == 0)
        {
            return;
        }

        var graph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var edges = new List<object>();
        var components = context.GetComponents(context.Project.ProjectRoot);
        if (components is not null)
        {
            foreach (var component in components.Values.Where(static item =>
                item.Reference.StartsWith("R", StringComparison.OrdinalIgnoreCase) && item.PadCount == 2))
            {
                var nets = context.GetPads(context.Project.ProjectRoot, component.Reference)?
                    .Select(static pad => pad.NetName)
                    .Where(static net => !string.IsNullOrWhiteSpace(net))
                    .Cast<string>()
                    .ToArray() ?? Array.Empty<string>();
                if (nets.Length != 2 || nets[0].Equals(nets[1], StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AddEdge(graph, nets[0], nets[1]);
                AddEdge(graph, nets[1], nets[0]);
                edges.Add(new { reference = component.Reference, from = nets[0], to = nets[1] });
            }
        }

        foreach (var assertion in context.Policy.DcFeedbackAssertions)
        {
            var path = FindPath(graph, assertion.FromNet, assertion.ToNet);
            context.Add(
                assertion.Id,
                "analogue-topology",
                path is null ? ReleaseAuditCheckStatus.Fail : ReleaseAuditCheckStatus.Pass,
                path is null
                    ? $"No resistor-only DC path exists from {assertion.FromNet} to {assertion.ToNet}."
                    : $"Resistive DC path exists: {string.Join(" -> ", path)}.",
                new { path, resistorEdges = edges });
        }
    }

    private static void AddEdge(Dictionary<string, HashSet<string>> graph, string from, string to)
    {
        if (!graph.TryGetValue(from, out var neighbors))
        {
            neighbors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            graph[from] = neighbors;
        }

        neighbors.Add(to);
    }

    private static IReadOnlyList<string>? FindPath(
        IReadOnlyDictionary<string, HashSet<string>> graph,
        string start,
        string end)
    {
        var queue = new Queue<(string Node, IReadOnlyList<string> Path)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        queue.Enqueue((start, new[] { start }));
        while (queue.TryDequeue(out var current))
        {
            if (current.Node.Equals(end, StringComparison.OrdinalIgnoreCase))
            {
                return current.Path;
            }

            if (!graph.TryGetValue(current.Node, out var neighbors))
            {
                continue;
            }

            foreach (var neighbor in neighbors.Where(seen.Add))
            {
                queue.Enqueue((neighbor, current.Path.Append(neighbor).ToArray()));
            }
        }

        return null;
    }

    private static void CheckMirrors(AuditContext context)
    {
        var components = context.GetComponents(context.Project.ProjectRoot);
        foreach (var pair in context.Policy.MirroredValuePairs)
        {
            if (pair.Count != 2)
            {
                context.Add(
                    "mirror-invalid-policy-entry",
                    "channel-symmetry",
                    ReleaseAuditCheckStatus.Fail,
                    "Mirrored value pairs must contain exactly two references.",
                    pair);
                continue;
            }

            var leftReference = pair[0];
            var rightReference = pair[1];
            var left = components?.GetValueOrDefault(leftReference);
            var right = components?.GetValueOrDefault(rightReference);
            var same = left?.Value is not null
                && right?.Value is not null
                && left.Value.Equals(right.Value, StringComparison.OrdinalIgnoreCase);
            context.Add(
                $"mirror-{leftReference}-{rightReference}",
                "channel-symmetry",
                same ? ReleaseAuditCheckStatus.Pass : ReleaseAuditCheckStatus.Fail,
                same
                    ? $"{leftReference} and {rightReference} both use {left!.Value}."
                    : $"Mirrored values differ or are missing: {leftReference} / {rightReference}.",
                new Dictionary<string, object?>
                {
                    [leftReference] = left?.Value,
                    [rightReference] = right?.Value
                });
        }
    }

    private static void CheckReferenceGroups(AuditContext context)
    {
        var targetComponents = context.GetComponents(context.Project.ProjectRoot);
        var policyDirectory = Path.GetDirectoryName(context.PolicyPath)!;
        foreach (var group in context.Policy.ReferenceGroups)
        {
            var status = group.Required ? ReleaseAuditCheckStatus.Fail : ReleaseAuditCheckStatus.Warn;
            var referenceProject = Path.IsPathRooted(group.ReferenceProject)
                ? Path.GetFullPath(group.ReferenceProject)
                : Path.GetFullPath(Path.Combine(policyDirectory, group.ReferenceProject));
            var referenceComponents = context.GetComponents(referenceProject);
            if (targetComponents is null || referenceComponents is null)
            {
                context.Add(
                    group.Id,
                    "reference-comparison",
                    status,
                    $"Reference or target project could not be inspected: {referenceProject}.");
                continue;
            }

            var differences = new List<object>();
            foreach (var mapping in group.Components)
            {
                var sourceRef = mapping.Key;
                var targetRef = mapping.Value;
                if (!referenceComponents.TryGetValue(sourceRef, out var source)
                    || !targetComponents.TryGetValue(targetRef, out var target))
                {
                    differences.Add(new
                    {
                        referenceComponent = sourceRef,
                        targetComponent = targetRef,
                        issue = "component missing"
                    });
                    continue;
                }

                if (group.CompareValues
                    && !string.Equals(source.Value, target.Value, StringComparison.OrdinalIgnoreCase))
                {
                    differences.Add(new
                    {
                        referenceComponent = sourceRef,
                        targetComponent = targetRef,
                        issue = "value mismatch",
                        referenceValue = source.Value,
                        targetValue = target.Value
                    });
                }

                var sourcePads = context.GetPads(referenceProject, sourceRef)?
                    .ToDictionary(static pad => pad.Name, StringComparer.OrdinalIgnoreCase);
                var targetPads = context.GetPads(context.Project.ProjectRoot, targetRef)?
                    .ToDictionary(static pad => pad.Name, StringComparer.OrdinalIgnoreCase);
                if (sourcePads is null || targetPads is null)
                {
                    differences.Add(new
                    {
                        referenceComponent = sourceRef,
                        targetComponent = targetRef,
                        issue = "pads unavailable"
                    });
                    continue;
                }

                if (!sourcePads.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(targetPads.Keys))
                {
                    differences.Add(new
                    {
                        referenceComponent = sourceRef,
                        targetComponent = targetRef,
                        issue = "pad-set mismatch",
                        referencePads = sourcePads.Keys.Order().ToArray(),
                        targetPads = targetPads.Keys.Order().ToArray()
                    });
                    continue;
                }

                var nonpolarTwoTerminal = sourcePads.Count == 2
                    && sourceRef.StartsWithAny("R", "C")
                    && targetRef.StartsWithAny("R", "C");
                if (nonpolarTwoTerminal)
                {
                    var expectedNets = sourcePads.Values
                        .Select(pad => MapNet(group.NetMap, pad.NetName))
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    var actualNets = targetPads.Values
                        .Select(static pad => pad.NetName ?? string.Empty)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (!actualNets.SequenceEqual(expectedNets, StringComparer.OrdinalIgnoreCase))
                    {
                        differences.Add(new
                        {
                            referenceComponent = sourceRef,
                            targetComponent = targetRef,
                            issue = "net-set mismatch",
                            expectedTargetNets = expectedNets,
                            actualTargetNets = actualNets
                        });
                    }

                    continue;
                }

                foreach (var sourcePad in sourcePads)
                {
                    var expected = MapNet(group.NetMap, sourcePad.Value.NetName);
                    var actual = targetPads[sourcePad.Key].NetName ?? string.Empty;
                    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    {
                        differences.Add(new
                        {
                            referenceComponent = sourceRef,
                            targetComponent = targetRef,
                            pad = sourcePad.Key,
                            issue = "net mismatch",
                            referenceNet = sourcePad.Value.NetName,
                            expectedTargetNet = expected,
                            actualTargetNet = actual
                        });
                    }
                }
            }

            context.Add(
                group.Id,
                "reference-comparison",
                differences.Count == 0 ? ReleaseAuditCheckStatus.Pass : status,
                differences.Count == 0
                    ? $"All {group.Components.Count} mapped components match the reference topology."
                    : $"Found {differences.Count} difference(s) from the known-good reference.",
                differences);
        }
    }

    private static string MapNet(IReadOnlyDictionary<string, string> map, string? net)
    {
        if (net is null)
        {
            return string.Empty;
        }

        if (map.TryGetValue(net, out var mapped))
        {
            return mapped;
        }

        var match = map.FirstOrDefault(pair => pair.Key.Equals(net, StringComparison.OrdinalIgnoreCase));
        return match.Key is not null ? match.Value : net;
    }

    private static void CheckManualEvidence(AuditContext context)
    {
        var required = context.Policy.ManualEvidence.RequiredItems;
        if (required.Count == 0)
        {
            return;
        }

        var configured = context.Policy.ManualEvidence.Path;
        var path = Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(context.Project.ProjectRoot, configured));
        var authorized = context.Projects.AuthorizePath(path);
        if (!authorized.Success || authorized.Data is null)
        {
            context.Add(
                "manual-release-signoff",
                "human-review",
                ReleaseAuditCheckStatus.Fail,
                authorized.Error?.Message ?? authorized.Summary);
            return;
        }
        path = authorized.Data;
        if (!File.Exists(path))
        {
            context.Add(
                "manual-release-signoff",
                "human-review",
                ReleaseAuditCheckStatus.Fail,
                $"Required manual release sign-off is missing: {path}.",
                new { requiredItems = required });
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var items = TryGet(document.RootElement, "items", out var itemArray)
            && itemArray.ValueKind == JsonValueKind.Array
                ? itemArray.EnumerateArray()
                    .Where(static item => ReadString(item, "id") is not null)
                    .ToDictionary(
                        static item => ReadString(item, "id")!,
                        static item => item,
                        StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var missing = required.Where(id =>
        {
            if (!items.TryGetValue(id, out var item))
            {
                return true;
            }

            return !string.Equals(ReadString(item, "status"), "approved", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(ReadString(item, "reviewer"))
                || string.IsNullOrWhiteSpace(ReadString(item, "reviewedAtUtc"))
                || string.IsNullOrWhiteSpace(ReadString(item, "evidence"));
        }).ToArray();
        context.Add(
            "manual-release-signoff",
            "human-review",
            missing.Length == 0 ? ReleaseAuditCheckStatus.Pass : ReleaseAuditCheckStatus.Fail,
            missing.Length == 0
                ? "All required manual reviews are signed with evidence."
                : $"Missing or incomplete manual sign-offs: {string.Join(", ", missing)}.",
            new { path, required, missing });
    }

    private static bool TryGet(JsonElement element, string property, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in element.EnumerateObject())
            {
                if (candidate.Name.Equals(property, StringComparison.OrdinalIgnoreCase))
                {
                    value = candidate.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string property)
    {
        return TryGet(element, property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool ReadBoolean(JsonElement element, string property)
    {
        return TryGet(element, property, out var value)
            && value.ValueKind == JsonValueKind.True;
    }

    private static string RenderMarkdown(ReleaseAuditResult report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# PCB release audit — {Path.GetFileName(report.Project.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}");
        builder.AppendLine();
        builder.AppendLine($"**Disposition: {report.Disposition}**");
        builder.AppendLine();
        builder.AppendLine($"- Generated: {report.GeneratedAtUtc:O}");
        builder.AppendLine($"- Project: `{report.Project}`");
        builder.AppendLine($"- Policy: `{report.Policy}`");
        builder.AppendLine($"- Results: {report.Summary.Pass} pass, {report.Summary.Fail} fail, {report.Summary.Warn} warn");
        builder.AppendLine();
        builder.AppendLine("## Gate results");
        builder.AppendLine();
        builder.AppendLine("| Status | Category | Check | Result |");
        builder.AppendLine("|---|---|---|---|");
        foreach (var check in report.Checks)
        {
            builder.AppendLine($"| {check.Status.ToString().ToUpperInvariant()} | {Escape(check.Category)} | `{Escape(check.Id)}` | {Escape(check.Summary)} |");
        }

        var findings = report.Checks.Where(static item => item.Status is ReleaseAuditCheckStatus.Fail or ReleaseAuditCheckStatus.Warn).ToArray();
        if (findings.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Findings");
            foreach (var finding in findings)
            {
                builder.AppendLine();
                builder.AppendLine($"### {finding.Status.ToString().ToUpperInvariant()} — {finding.Id}");
                builder.AppendLine();
                builder.AppendLine(finding.Summary);
                builder.AppendLine();
                builder.AppendLine("```json");
                builder.AppendLine(JsonSerializer.Serialize(finding.Evidence, JsonOptions));
                builder.AppendLine("```");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Source fingerprints");
        builder.AppendLine();
        foreach (var source in report.SourceFiles)
        {
            builder.AppendLine($"- `{source.Path}` — SHA-256 `{source.Sha256}`");
        }

        builder.AppendLine();
        builder.AppendLine("## Decision rule");
        builder.AppendLine();
        builder.AppendLine("Fabrication is permitted only when the disposition is `READY`. `BLOCKED` means required evidence or a required electrical relationship is missing.");
        return builder.ToString();
    }

    private static string Escape(string text)
    {
        return text.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");
    }

    private sealed class AuditContext
    {
        private readonly ComponentService _components;
        private readonly BoardInspectionService _inspection;
        private readonly Dictionary<string, IReadOnlyDictionary<string, ComponentSummary>?> _componentCache =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<PadSummary>?> _padCache =
            new(StringComparer.OrdinalIgnoreCase);

        public AuditContext(
            ProjectSummary project,
            string policyPath,
            ReleaseAuditPolicy policy,
            ProjectDiscoveryService projects,
            ComponentService components,
            BoardInspectionService inspection,
            RoutingService routing,
            TestSpecService tests,
            BestPracticeReviewService bestPractices,
            Func<SimulationCapabilities> simulationCapabilities)
        {
            Project = project;
            PolicyPath = policyPath;
            Policy = policy;
            Projects = projects;
            _components = components;
            _inspection = inspection;
            Routing = routing;
            Tests = tests;
            BestPractices = bestPractices;
            SimulationCapabilities = simulationCapabilities;
        }

        public ProjectSummary Project { get; }
        public string PolicyPath { get; }
        public ReleaseAuditPolicy Policy { get; }
        public ProjectDiscoveryService Projects { get; }
        public RoutingService Routing { get; }
        public TestSpecService Tests { get; }
        public BestPracticeReviewService BestPractices { get; }
        public Func<SimulationCapabilities> SimulationCapabilities { get; }
        public bool ZoneAwareDrcConnectivityPassed { get; set; }
        public int? LatestDrcFindingCount { get; set; }
        public List<ReleaseAuditCheck> Checks { get; } = new();
        public List<ReleaseAuditSourceFile> SourceFiles { get; } = new();

        public void Add(
            string id,
            string category,
            ReleaseAuditCheckStatus status,
            string summary,
            object? evidence = null)
        {
            Checks.Add(new ReleaseAuditCheck(id, category, status, summary, evidence));
        }

        public IReadOnlyDictionary<string, ComponentSummary>? GetComponents(string project)
        {
            var root = Path.GetFullPath(project);
            if (_componentCache.TryGetValue(root, out var cached))
            {
                return cached;
            }

            var result = _components.ListComponents(root);
            var components = result.Success && result.Data is not null
                ? result.Data.Components.ToDictionary(static item => item.Reference, StringComparer.OrdinalIgnoreCase)
                : null;
            _componentCache[root] = components;
            return components;
        }

        public IReadOnlyList<PadSummary>? GetPads(string project, string reference)
        {
            var key = $"{Path.GetFullPath(project)}\0{reference}";
            if (_padCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var result = _inspection.ListFootprintPads(project, reference);
            var pads = result.Success ? result.Data?.Pads : null;
            _padCache[key] = pads;
            return pads;
        }
    }

    private sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);
}

internal static class ReleaseAuditStringExtensions
{
    public static bool StartsWithAny(this string value, params string[] prefixes)
    {
        return prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}

public static class ReleaseAuditDispositions
{
    public const string Ready = "READY";
    public const string PrototypeOnly = "PROTOTYPE-ONLY";
    public const string Blocked = "BLOCKED";
}

[JsonConverter(typeof(ReleaseAuditCheckStatusJsonConverter))]
public enum ReleaseAuditCheckStatus
{
    Pass,
    Fail,
    Warn
}

public sealed class ReleaseAuditCheckStatusJsonConverter : JsonConverter<ReleaseAuditCheckStatus>
{
    public override ReleaseAuditCheckStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String
            || !Enum.TryParse<ReleaseAuditCheckStatus>(reader.GetString(), true, out var value))
        {
            throw new JsonException("Release audit check status must be PASS, FAIL, or WARN.");
        }

        return value;
    }

    public override void Write(Utf8JsonWriter writer, ReleaseAuditCheckStatus value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString().ToUpperInvariant());
    }
}

public sealed record ReleaseAuditResult(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string Project,
    string Policy,
    string? PolicyName,
    string Disposition,
    ReleaseAuditSummary Summary,
    IReadOnlyList<ReleaseAuditSourceFile> SourceFiles,
    IReadOnlyList<ReleaseAuditCheck> Checks,
    string JsonReportPath,
    string MarkdownReportPath);

public sealed record ReleaseAuditSummary(int Pass, int Fail, int Warn);

public sealed record ReleaseAuditSourceFile(string Path, string Sha256, DateTimeOffset ModifiedUtc);

public sealed record ReleaseAuditCheck(
    string Id,
    string Category,
    ReleaseAuditCheckStatus Status,
    string Summary,
    object? Evidence);

public sealed class ReleaseAuditPolicy
{
    public int Version { get; init; }
    public string? Name { get; init; }
    public ReleaseAuditGitPolicy Git { get; init; } = new();
    public ReleaseAuditEvidencePolicy ReleaseEvidence { get; init; } = new();
    public ReleaseAuditBestPracticePolicy BestPracticeReview { get; init; } = new();
    public ReleaseAuditSimulationPolicy Simulation { get; init; } = new();
    public ReleaseAuditIntentPolicy Intent { get; init; } = new();
    public IReadOnlyList<ReleaseAuditPinNetAssertion> PinNetAssertions { get; init; } = Array.Empty<ReleaseAuditPinNetAssertion>();
    public IReadOnlyList<ReleaseAuditNetSetAssertion> NetSetAssertions { get; init; } = Array.Empty<ReleaseAuditNetSetAssertion>();
    public IReadOnlyList<ReleaseAuditDcFeedbackAssertion> DcFeedbackAssertions { get; init; } = Array.Empty<ReleaseAuditDcFeedbackAssertion>();
    public IReadOnlyList<IReadOnlyList<string>> MirroredValuePairs { get; init; } = Array.Empty<IReadOnlyList<string>>();
    public ReleaseAuditManualEvidencePolicy ManualEvidence { get; init; } = new();
    public IReadOnlyList<ReleaseAuditReferenceGroup> ReferenceGroups { get; init; } = Array.Empty<ReleaseAuditReferenceGroup>();
}

public sealed class ReleaseAuditGitPolicy
{
    public bool Required { get; init; }
    public bool RequireClean { get; init; }
}

public sealed class ReleaseAuditEvidencePolicy
{
    public bool Required { get; init; }
    public bool RequireFresh { get; init; }
    public IReadOnlyList<string> RequiredChecks { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockingAssemblyDiagnosticCodes { get; init; } = Array.Empty<string>();
}

public sealed class ReleaseAuditBestPracticePolicy
{
    public bool Required { get; init; }
    public bool RequireFresh { get; init; } = true;
    public bool AllowPassWithConcerns { get; init; }
}

public sealed class ReleaseAuditSimulationPolicy
{
    public bool Required { get; init; }
    public int MinimumTests { get; init; } = 1;
}

public sealed class ReleaseAuditIntentPolicy
{
    public IReadOnlyList<string> RequiredVoltageLimits { get; init; } = Array.Empty<string>();
}

public sealed record ReleaseAuditPinNetAssertion(string Id, string Reference, string Pad, string Net);

public sealed record ReleaseAuditNetSetAssertion(string Id, string Reference, IReadOnlyList<string> Nets);

public sealed record ReleaseAuditDcFeedbackAssertion(string Id, string FromNet, string ToNet);

public sealed class ReleaseAuditManualEvidencePolicy
{
    public string Path { get; init; } = System.IO.Path.Combine(".pcbhelper", "release-signoff.json");
    public IReadOnlyList<string> RequiredItems { get; init; } = Array.Empty<string>();
}

public sealed class ReleaseAuditReferenceGroup
{
    public string Id { get; init; } = string.Empty;
    public string ReferenceProject { get; init; } = string.Empty;
    public bool Required { get; init; } = true;
    public bool CompareValues { get; init; } = true;
    public IReadOnlyDictionary<string, string> NetMap { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> Components { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
