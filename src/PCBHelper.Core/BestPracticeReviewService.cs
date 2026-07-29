using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PCBHelper.Core;

public sealed class BestPracticeReviewService
{
    public const int PromptVersion = 1;
    public const int AssessmentVersion = 1;
    private const string PromptResourceSuffix = "best-practice-review-prompt-v1.md";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly Regex PassivePackagePattern = new(
        "(?<!\\d)(0201|0402|0603|0805|0806|1206|1210|1812)(?!\\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ProjectDiscoveryService _projects;
    private readonly BoardSummaryService _boardSummary;
    private readonly BoardInspectionService _inspection;
    private readonly ComponentService _components;
    private readonly RoutingService _routing;
    private readonly SchematicAuthoringService _schematic;
    private readonly TestSpecService _tests;
    private readonly Func<DateTimeOffset> _clock;

    public BestPracticeReviewService(
        ProjectDiscoveryService projects,
        BoardSummaryService boardSummary,
        BoardInspectionService inspection,
        ComponentService components,
        RoutingService routing,
        SchematicAuthoringService schematic,
        TestSpecService tests,
        Func<DateTimeOffset>? clock = null)
    {
        _projects = projects;
        _boardSummary = boardSummary;
        _inspection = inspection;
        _components = components;
        _routing = routing;
        _schematic = schematic;
        _tests = tests;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public ToolResponse<BestPracticeReviewPreparation> Prepare(string projectPath)
    {
        var project = _projects.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
            return ToolResponse<BestPracticeReviewPreparation>.Fail(
                project.Summary,
                project.Error?.Code ?? "PROJECT_NOT_FOUND",
                project.Error?.Message);

        try
        {
            var evidence = CollectEvidence(project.Data);
            var canonicalEvidence = JsonSerializer.Serialize(evidence, JsonOptions);
            var evidenceHash = Sha256(Encoding.UTF8.GetBytes(canonicalEvidence));
            var reviewId = $"bp-{evidenceHash[..16]}";
            var prompt = BuildPrompt(reviewId, evidenceHash, evidence);
            var missingVisuals = evidence.Items.Single(static item => item.Id == "visual.artifacts").Available
                ? Array.Empty<string>()
                : new[] { "No current schematic/PCB render evidence was found. Visual criteria must be unableToAssess until generate_review_package has produced review artifacts and the images have actually been inspected." };

            return ToolResponse<BestPracticeReviewPreparation>.Ok(
                "Prepared an evidence-bound best-practice review prompt.",
                new BestPracticeReviewPreparation(
                    PromptVersion,
                    reviewId,
                    evidenceHash,
                    project.Data.ProjectRoot,
                    BestPracticeRuleCatalog.All,
                    evidence,
                    prompt,
                    BestPracticeAssessmentSchema.Create()),
                missingVisuals);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return ToolResponse<BestPracticeReviewPreparation>.Fail(
                "Could not prepare best-practice review evidence.",
                "BEST_PRACTICE_PREPARE_FAILED",
                exception.Message);
        }
    }

    public ToolResponse<BestPracticeReviewReport> Submit(
        string projectPath,
        string assessmentJson,
        string expectedEvidenceHash)
    {
        var current = Prepare(projectPath);
        if (!current.Success || current.Data is null)
            return ToolResponse<BestPracticeReviewReport>.Fail(
                current.Summary,
                current.Error?.Code ?? "BEST_PRACTICE_PREPARE_FAILED",
                current.Error?.Message);
        if (string.IsNullOrWhiteSpace(expectedEvidenceHash) ||
            !current.Data.EvidenceHash.Equals(expectedEvidenceHash, StringComparison.OrdinalIgnoreCase))
            return ToolResponse<BestPracticeReviewReport>.Fail(
                "The design evidence changed after the review prompt was prepared.",
                "BEST_PRACTICE_EVIDENCE_STALE",
                $"Expected {expectedEvidenceHash}; current evidence hash is {current.Data.EvidenceHash}.");

        BestPracticeAssessment? assessment;
        try
        {
            assessment = JsonSerializer.Deserialize<BestPracticeAssessment>(assessmentJson, JsonOptions);
        }
        catch (JsonException exception)
        {
            return ToolResponse<BestPracticeReviewReport>.Fail(
                "The LLM best-practice assessment is invalid JSON.",
                "BEST_PRACTICE_ASSESSMENT_INVALID",
                exception.Message);
        }
        if (assessment is null)
            return ToolResponse<BestPracticeReviewReport>.Fail(
                "The LLM best-practice assessment is empty.",
                "BEST_PRACTICE_ASSESSMENT_INVALID");

        var errors = ValidateAssessment(current.Data, assessment);
        if (errors.Count > 0)
            return ToolResponse<BestPracticeReviewReport>.Fail(
                "The LLM best-practice assessment did not satisfy the review contract.",
                "BEST_PRACTICE_ASSESSMENT_INVALID",
                string.Join(" ", errors));

        try
        {
            var disposition = DeriveDisposition(assessment);
            var generatedAt = _clock();
            var runId = $"{generatedAt.UtcDateTime:yyyyMMddTHHmmssfffZ}-{current.Data.EvidenceHash[..8]}";
            var output = Path.Combine(current.Data.ProjectRoot, ".pcbhelper", "best-practice-reviews", runId);
            var authorized = _projects.AuthorizePath(output);
            if (!authorized.Success || authorized.Data is null)
                return ToolResponse<BestPracticeReviewReport>.Fail(
                    authorized.Summary,
                    authorized.Error?.Code ?? "PROJECT_SCOPE_VIOLATION",
                    authorized.Error?.Message);
            Directory.CreateDirectory(authorized.Data);

            var assessmentPath = Path.Combine(authorized.Data, "assessment.json");
            var evidencePath = Path.Combine(authorized.Data, "evidence.json");
            var promptPath = Path.Combine(authorized.Data, "prompt.md");
            var reportPath = Path.Combine(authorized.Data, "report.json");
            var markdownPath = Path.Combine(authorized.Data, "report.md");
            var findings = BestPracticeRuleCatalog.All.Select(rule =>
            {
                var criterion = assessment.Criteria.Single(item => item.RuleId == rule.Id);
                return new BestPracticeReviewFinding(
                    rule.Id,
                    rule.Title,
                    rule.Severity,
                    criterion.Status,
                    criterion.Confidence,
                    criterion.Summary,
                    criterion.EvidenceIds,
                    criterion.Recommendation);
            }).ToArray();
            var report = new BestPracticeReviewReport(
                1,
                runId,
                generatedAt,
                current.Data.ProjectRoot,
                current.Data.EvidenceHash,
                PromptVersion,
                assessment.Reviewer,
                disposition,
                findings,
                assessment.UnresolvedQuestions,
                assessment.Limitations,
                reportPath,
                markdownPath,
                new[] { assessmentPath, evidencePath, promptPath, reportPath, markdownPath });

            File.WriteAllText(assessmentPath, JsonSerializer.Serialize(assessment, JsonOptions) + Environment.NewLine);
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(current.Data.Evidence, JsonOptions) + Environment.NewLine);
            File.WriteAllText(promptPath, current.Data.Prompt);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine);
            File.WriteAllText(markdownPath, RenderMarkdown(report));

            return ToolResponse<BestPracticeReviewReport>.Ok(
                $"Best-practice review disposition: {disposition}.",
                report,
                disposition == BestPracticeDisposition.Pass
                    ? Array.Empty<string>()
                    : new[] { "This report contains LLM judgment. A qualified engineer must resolve material findings before release." });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ToolResponse<BestPracticeReviewReport>.Fail(
                "Could not write best-practice review artifacts.",
                "BEST_PRACTICE_REPORT_WRITE_FAILED",
                exception.Message);
        }
    }

    public ToolResponse<BestPracticeReviewReport> GetReport(string projectPath, string runId)
    {
        var project = _projects.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
            return ToolResponse<BestPracticeReviewReport>.Fail(
                project.Summary,
                project.Error?.Code ?? "PROJECT_NOT_FOUND",
                project.Error?.Message);
        if (string.IsNullOrWhiteSpace(runId) ||
            runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            runId.Contains("..", StringComparison.Ordinal))
            return ToolResponse<BestPracticeReviewReport>.Fail(
                "Invalid best-practice review run id.",
                "BEST_PRACTICE_REPORT_NOT_FOUND");
        var path = Path.Combine(project.Data.ProjectRoot, ".pcbhelper", "best-practice-reviews", runId, "report.json");
        if (!File.Exists(path))
            return ToolResponse<BestPracticeReviewReport>.Fail(
                "Best-practice review report was not found.",
                "BEST_PRACTICE_REPORT_NOT_FOUND");
        try
        {
            var report = JsonSerializer.Deserialize<BestPracticeReviewReport>(File.ReadAllText(path), JsonOptions);
            return report is null
                ? ToolResponse<BestPracticeReviewReport>.Fail("Best-practice review report is invalid.", "BEST_PRACTICE_REPORT_INVALID")
                : ToolResponse<BestPracticeReviewReport>.Ok("Loaded best-practice review report.", report);
        }
        catch (JsonException exception)
        {
            return ToolResponse<BestPracticeReviewReport>.Fail(
                "Best-practice review report is invalid.",
                "BEST_PRACTICE_REPORT_INVALID",
                exception.Message);
        }
    }

    public ToolResponse<BestPracticeReviewStatus> ValidateCurrent(string projectPath, string? runId = null)
    {
        var project = _projects.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
            return ToolResponse<BestPracticeReviewStatus>.Fail(
                project.Summary,
                project.Error?.Code ?? "PROJECT_NOT_FOUND",
                project.Error?.Message);
        var selectedRun = runId;
        if (string.IsNullOrWhiteSpace(selectedRun))
        {
            var root = Path.Combine(project.Data.ProjectRoot, ".pcbhelper", "best-practice-reviews");
            selectedRun = Directory.Exists(root)
                ? Directory.GetDirectories(root)
                    .Select(Path.GetFileName)
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .OrderByDescending(static name => name, StringComparer.Ordinal)
                    .FirstOrDefault()
                : null;
        }
        if (string.IsNullOrWhiteSpace(selectedRun))
            return ToolResponse<BestPracticeReviewStatus>.Fail(
                "No best-practice review report exists for this project.",
                "BEST_PRACTICE_REPORT_NOT_FOUND");

        var report = GetReport(project.Data.ProjectRoot, selectedRun);
        if (!report.Success || report.Data is null)
            return ToolResponse<BestPracticeReviewStatus>.Fail(
                report.Summary,
                report.Error?.Code ?? "BEST_PRACTICE_REPORT_NOT_FOUND",
                report.Error?.Message);
        var current = Prepare(project.Data.ProjectRoot);
        if (!current.Success || current.Data is null)
            return ToolResponse<BestPracticeReviewStatus>.Fail(
                current.Summary,
                current.Error?.Code ?? "BEST_PRACTICE_PREPARE_FAILED",
                current.Error?.Message);
        var evidenceCurrent = report.Data.EvidenceHash.Equals(current.Data.EvidenceHash, StringComparison.OrdinalIgnoreCase);
        var passed = evidenceCurrent && report.Data.Disposition == BestPracticeDisposition.Pass;
        return ToolResponse<BestPracticeReviewStatus>.Ok(
            passed
                ? "The latest best-practice review is current and passed without concerns."
                : evidenceCurrent
                    ? $"The best-practice review is current but disposition is {report.Data.Disposition}."
                    : "The best-practice review is stale because project evidence changed.",
            new BestPracticeReviewStatus(
                report.Data.RunId,
                report.Data.EvidenceHash,
                current.Data.EvidenceHash,
                evidenceCurrent,
                report.Data.Disposition,
                passed,
                report.Data.ReportPath));
    }

    private BestPracticeEvidenceBundle CollectEvidence(ProjectSummary project)
    {
        var items = new List<BestPracticeEvidenceItem>
        {
            Available("project.summary", "Resolved KiCad project files and missing-file state.", project)
        };

        Add(items, "schematic.context", "Schematic symbols, positions, fields, wires, and labels.", _schematic.ListSymbols(project.ProjectRoot));
        Add(items, "board.summary", "PCB footprint names, sides, positions, and rotations.", _boardSummary.GetSummary(project.ProjectRoot));
        Add(items, "board.nets", "PCB nets with pad, track, and via counts.", _inspection.ListNets(project.ProjectRoot));
        Add(items, "board.tracks", "PCB track geometry, layers, net names, and widths.", _routing.ListTracks(project.ProjectRoot));
        Add(items, "board.vias", "PCB via geometry, layers, sizes, and net names.", _routing.ListVias(project.ProjectRoot));
        var componentResponse = _components.ListComponents(project.ProjectRoot);
        Add(items, "components", "Merged schematic/PCB component values, footprints, positions, and pad counts.", componentResponse);
        Add(items, "simulation.tests", "Declared project test and simulation specifications.", _tests.ListTests(project.ProjectRoot));
        items.Add(LoadOptionalJson(
            project.ProjectRoot,
            "design.intent",
            "Structured project design intent and declared evidence.",
            Path.Combine(project.ProjectRoot, ".pcbhelper", "design-intent.json")));
        items.Add(CollectVisualArtifacts(project));

        var components = componentResponse.Data?.Components ?? Array.Empty<ComponentSummary>();
        var tracks = _routing.ListTracks(project.ProjectRoot).Data?.Tracks ?? Array.Empty<TrackSummary>();
        var vias = _routing.ListVias(project.ProjectRoot).Data?.Vias ?? Array.Empty<ViaSummary>();
        var observations = CreateAutomatedObservations(components, tracks, vias);
        items.Add(Available(
            "automated.observations",
            "Deterministic heuristics that support, but do not replace, engineering judgment.",
            observations));

        var sourceFiles = SourceFiles(project, items);
        return new BestPracticeEvidenceBundle(
            1,
            project.ProjectRoot,
            sourceFiles,
            items);
    }

    private static IReadOnlyList<BestPracticeAutomatedObservation> CreateAutomatedObservations(
        IReadOnlyList<ComponentSummary> components,
        IReadOnlyList<TrackSummary> tracks,
        IReadOnlyList<ViaSummary> vias)
    {
        var smallPackages = components
            .Where(static component => component.FootprintName is not null)
            .Select(component => new { component.Reference, component.FootprintName, Package = PassivePackagePattern.Match(component.FootprintName!).Value })
            .Where(static item => item.Package is "0201" or "0402" or "0603")
            .Select(static item => $"{item.Reference}:{item.FootprintName}")
            .ToArray();
        var testpoints = components
            .Where(static component => component.Reference.StartsWith("TP", StringComparison.OrdinalIgnoreCase) ||
                                       component.FootprintName?.Contains("TestPoint", StringComparison.OrdinalIgnoreCase) == true)
            .Select(static component => component.Reference)
            .ToArray();
        var currentMeasurementCandidates = components
            .Where(static component =>
                component.Reference.StartsWith("JP", StringComparison.OrdinalIgnoreCase) ||
                component.FootprintName?.Contains("Jumper", StringComparison.OrdinalIgnoreCase) == true ||
                component.Value?.Contains("mR", StringComparison.OrdinalIgnoreCase) == true ||
                component.Value?.Equals("0R", StringComparison.OrdinalIgnoreCase) == true)
            .Select(static component => $"{component.Reference}:{component.Value}")
            .ToArray();
        var powerTracks = tracks
            .Where(static track => IsPowerNet(track.NetName))
            .Select(static track => new { track.NetName, track.WidthMillimeters, track.Layer })
            .ToArray();
        var groundVias = vias.Count(static via => IsGroundNet(via.NetName));

        return new[]
        {
            new BestPracticeAutomatedObservation(
                "PROTO-SMALL-PACKAGES",
                smallPackages.Length == 0 ? "No 0603-or-smaller footprint names were detected." : $"Detected {smallPackages.Length} component(s) using 0603-or-smaller footprint names.",
                smallPackages),
            new BestPracticeAutomatedObservation(
                "DFT-TESTPOINTS",
                $"Detected {testpoints.Length} explicit testpoint reference(s).",
                testpoints),
            new BestPracticeAutomatedObservation(
                "DFT-CURRENT-MEASUREMENT",
                $"Detected {currentMeasurementCandidates.Length} possible jumper/shunt current-measurement component(s).",
                currentMeasurementCandidates),
            new BestPracticeAutomatedObservation(
                "PWR-TRACK-WIDTHS",
                $"Detected {powerTracks.Length} routed power/ground track segment(s).",
                powerTracks.Select(static track => $"{track.NetName}:{track.WidthMillimeters}mm:{track.Layer}").ToArray()),
            new BestPracticeAutomatedObservation(
                "GND-VIAS",
                $"Detected {groundVias} via(s) explicitly assigned to a ground-named net.",
                Array.Empty<string>())
        };
    }

    private static IReadOnlyList<BestPracticeSourceFile> SourceFiles(
        ProjectSummary project,
        IReadOnlyList<BestPracticeEvidenceItem> evidence)
    {
        var candidates = new[] { project.ProjectFile, project.SchematicFile, project.BoardFile }
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Concat(evidence
                .Where(static item => item.Id is "design.intent" or "visual.artifacts")
                .SelectMany(static item => item.SourceFiles))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase);
        return candidates.Select(path => new BestPracticeSourceFile(
            Path.GetFullPath(path),
            Sha256(File.ReadAllBytes(path)),
            new FileInfo(path).Length)).ToArray();
    }

    private static BestPracticeEvidenceItem CollectVisualArtifacts(ProjectSummary project)
    {
        var reviewRoot = Path.Combine(project.ProjectRoot, ".pcbhelper", "review");
        if (!Directory.Exists(reviewRoot))
            return Unavailable(
                "visual.artifacts",
                "Current schematic and PCB renders for visual judgment.",
                "No .pcbhelper/review directory exists. Generate a current review package first.");
        var latest = Directory.GetDirectories(reviewRoot)
            .OrderByDescending(static path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (latest is null)
            return Unavailable(
                "visual.artifacts",
                "Current schematic and PCB renders for visual judgment.",
                "The review directory contains no review run.");
        var files = Directory.GetFiles(latest, "*", SearchOption.AllDirectories)
            .Where(static path =>
                path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var newestDesignWrite = new[] { project.SchematicFile, project.BoardFile }
            .Where(static path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Cast<string>()
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();
        if (files.Length > 0 && files.Any(path => File.GetLastWriteTimeUtc(path) < newestDesignWrite))
            return Unavailable(
                "visual.artifacts",
                "Current schematic and PCB renders for visual judgment.",
                "The latest visual review artifacts are older than the current schematic or PCB. Regenerate the review package.");
        return files.Length == 0
            ? Unavailable(
                "visual.artifacts",
                "Current schematic and PCB renders for visual judgment.",
                "The latest review run contains no PNG, SVG, or PDF render.")
            : new BestPracticeEvidenceItem(
                "visual.artifacts",
                "Current schematic and PCB renders for visual judgment.",
                true,
                JsonSerializer.SerializeToElement(files, JsonOptions),
                null,
                files);
    }

    private static BestPracticeEvidenceItem LoadOptionalJson(
        string projectRoot,
        string id,
        string description,
        string path)
    {
        if (!File.Exists(path))
            return Unavailable(id, description, $"Optional evidence does not exist: {Path.GetRelativePath(projectRoot, path)}.");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return new BestPracticeEvidenceItem(
                id,
                description,
                true,
                document.RootElement.Clone(),
                null,
                new[] { path });
        }
        catch (JsonException exception)
        {
            return Unavailable(id, description, $"Evidence JSON is invalid: {exception.Message}");
        }
    }

    private static void Add<T>(
        ICollection<BestPracticeEvidenceItem> target,
        string id,
        string description,
        ToolResponse<T> response)
    {
        target.Add(response.Success && response.Data is not null
            ? Available(id, description, response.Data)
            : Unavailable(id, description, response.Error?.Message ?? response.Summary));
    }

    private static BestPracticeEvidenceItem Available<T>(string id, string description, T data) =>
        new(
            id,
            description,
            true,
            JsonSerializer.SerializeToElement(data, JsonOptions),
            null,
            Array.Empty<string>());

    private static BestPracticeEvidenceItem Unavailable(string id, string description, string limitation) =>
        new(
            id,
            description,
            false,
            JsonSerializer.SerializeToElement(new { }, JsonOptions),
            limitation,
            Array.Empty<string>());

    private static IReadOnlyList<string> ValidateAssessment(
        BestPracticeReviewPreparation preparation,
        BestPracticeAssessment assessment)
    {
        var errors = new List<string>();
        if (assessment.Version != AssessmentVersion)
            errors.Add($"assessment.version must be {AssessmentVersion}.");
        if (assessment.PromptVersion != PromptVersion)
            errors.Add($"assessment.promptVersion must be {PromptVersion}.");
        if (!assessment.ReviewId.Equals(preparation.ReviewId, StringComparison.Ordinal))
            errors.Add("assessment.reviewId does not match the prepared review.");
        if (!assessment.EvidenceHash.Equals(preparation.EvidenceHash, StringComparison.OrdinalIgnoreCase))
            errors.Add("assessment.evidenceHash does not match the prepared evidence.");
        if (string.IsNullOrWhiteSpace(assessment.Reviewer.Model))
            errors.Add("assessment.reviewer.model is required.");

        var knownEvidence = preparation.Evidence.Items.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        var grouped = assessment.Criteria.GroupBy(static criterion => criterion.RuleId, StringComparer.Ordinal).ToArray();
        foreach (var duplicate in grouped.Where(static group => group.Count() > 1))
            errors.Add($"Criterion {duplicate.Key} occurs more than once.");
        foreach (var unknown in grouped.Where(group => !BestPracticeRuleCatalog.ById.ContainsKey(group.Key)))
            errors.Add($"Unknown best-practice rule: {unknown.Key}.");

        foreach (var rule in BestPracticeRuleCatalog.All)
        {
            var criterion = assessment.Criteria.SingleOrDefault(item => item.RuleId == rule.Id);
            if (criterion is null)
            {
                errors.Add($"Missing criterion response for {rule.Id}.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(criterion.Summary))
                errors.Add($"Criterion {rule.Id} requires a concise summary.");
            foreach (var evidenceId in criterion.EvidenceIds)
                if (!knownEvidence.ContainsKey(evidenceId))
                    errors.Add($"Criterion {rule.Id} cites unknown evidence id {evidenceId}.");
            if (criterion.Status is not BestPracticeCriterionStatus.UnableToAssess &&
                criterion.EvidenceIds.Count == 0)
                errors.Add($"Criterion {rule.Id} must cite evidence.");
            if (criterion.Status is not BestPracticeCriterionStatus.UnableToAssess &&
                !criterion.EvidenceIds.Any(rule.SuggestedEvidenceIds.Contains))
                errors.Add($"Criterion {rule.Id} must cite at least one relevant evidence item.");
            if (rule.RequiresVisualEvidence &&
                criterion.Status is not (BestPracticeCriterionStatus.UnableToAssess or BestPracticeCriterionStatus.NotApplicable) &&
                (!knownEvidence.TryGetValue("visual.artifacts", out var visualEvidence) ||
                 !visualEvidence.Available ||
                 !criterion.EvidenceIds.Contains("visual.artifacts", StringComparer.Ordinal)))
                errors.Add($"Criterion {rule.Id} requires inspected visual.artifacts evidence or must be unableToAssess/notApplicable.");
            var relevantAvailable = rule.SuggestedEvidenceIds
                .Where(knownEvidence.ContainsKey)
                .Any(id => knownEvidence[id].Available);
            if (!relevantAvailable && criterion.Status is not BestPracticeCriterionStatus.UnableToAssess)
                errors.Add($"Criterion {rule.Id} has no available relevant evidence and must be unableToAssess.");
            if (criterion.Status == BestPracticeCriterionStatus.NotApplicable &&
                string.IsNullOrWhiteSpace(criterion.ApplicabilityRationale))
                errors.Add($"Criterion {rule.Id} requires applicabilityRationale when marked notApplicable.");
            if (criterion.Status is BestPracticeCriterionStatus.Fail or BestPracticeCriterionStatus.Concern &&
                string.IsNullOrWhiteSpace(criterion.Recommendation))
                errors.Add($"Criterion {rule.Id} requires a recommendation for fail or concern.");
        }
        return errors;
    }

    private static BestPracticeDisposition DeriveDisposition(BestPracticeAssessment assessment)
    {
        if (assessment.Criteria.Any(static criterion => criterion.Status == BestPracticeCriterionStatus.Fail))
            return BestPracticeDisposition.Revise;
        if (assessment.Criteria.Any(static criterion => criterion.Status == BestPracticeCriterionStatus.UnableToAssess))
            return BestPracticeDisposition.UnableToAssess;
        if (assessment.Criteria.Any(static criterion => criterion.Status == BestPracticeCriterionStatus.Concern))
            return BestPracticeDisposition.PassWithConcerns;
        return BestPracticeDisposition.Pass;
    }

    private static string BuildPrompt(
        string reviewId,
        string evidenceHash,
        BestPracticeEvidenceBundle evidence)
    {
        var template = ReadEmbeddedPrompt();
        return template
            .Replace("{{REVIEW_ID}}", reviewId, StringComparison.Ordinal)
            .Replace("{{PROMPT_VERSION}}", PromptVersion.ToString(), StringComparison.Ordinal)
            .Replace("{{EVIDENCE_HASH}}", evidenceHash, StringComparison.Ordinal)
            .Replace("{{RULES_JSON}}", JsonSerializer.Serialize(BestPracticeRuleCatalog.All, JsonOptions), StringComparison.Ordinal)
            .Replace("{{EVIDENCE_JSON}}", JsonSerializer.Serialize(evidence, JsonOptions), StringComparison.Ordinal)
            .Replace("{{ASSESSMENT_SCHEMA_JSON}}", BestPracticeAssessmentSchema.Create(), StringComparison.Ordinal);
    }

    private static string ReadEmbeddedPrompt()
    {
        var assembly = typeof(BestPracticeReviewService).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(resource => resource.EndsWith(PromptResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name) ??
                           throw new InvalidOperationException("Embedded best-practice review prompt is unavailable.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string RenderMarkdown(BestPracticeReviewReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# PCBHelper best-practice review");
        builder.AppendLine();
        builder.AppendLine($"- Run: `{report.RunId}`");
        builder.AppendLine($"- Generated: {report.GeneratedAtUtc:O}");
        builder.AppendLine($"- Evidence hash: `{report.EvidenceHash}`");
        builder.AppendLine($"- Reviewer: `{report.Reviewer.Kind}` / `{report.Reviewer.Model}`");
        builder.AppendLine($"- Disposition: **{report.Disposition}**");
        builder.AppendLine();
        builder.AppendLine("| Rule | Severity | Status | Confidence | Summary |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var finding in report.Findings)
            builder.AppendLine($"| `{finding.RuleId}` | {finding.Severity} | {finding.Status} | {finding.Confidence} | {EscapeMarkdown(finding.Summary)} |");
        if (report.UnresolvedQuestions.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Unresolved questions");
            builder.AppendLine();
            foreach (var question in report.UnresolvedQuestions) builder.AppendLine($"- {question}");
        }
        if (report.Limitations.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Limitations");
            builder.AppendLine();
            foreach (var limitation in report.Limitations) builder.AppendLine($"- {limitation}");
        }
        builder.AppendLine();
        builder.AppendLine("This is an evidence-bound LLM review. It does not prove electrical function, EMC compliance, safety, or manufacturability.");
        return builder.ToString();
    }

    private static string EscapeMarkdown(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    private static bool IsGroundNet(string? net) => !string.IsNullOrWhiteSpace(net) && (net.Equals("GND", StringComparison.OrdinalIgnoreCase) || net.Contains("GROUND", StringComparison.OrdinalIgnoreCase));
    private static bool IsPowerNet(string? net) => IsGroundNet(net) || (!string.IsNullOrWhiteSpace(net) &&
        (net.Contains("VCC", StringComparison.OrdinalIgnoreCase) ||
         net.Contains("VDD", StringComparison.OrdinalIgnoreCase) ||
         net.Contains("VIN", StringComparison.OrdinalIgnoreCase) ||
         Regex.IsMatch(net, "(^|[^0-9])(3V3|5V|12V|24V)([^0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)));
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public static class BestPracticeRuleCatalog
{
    public static readonly IReadOnlyList<BestPracticeRule> All = new[]
    {
        Rule("ARCH-SOC-001", "Functional separation", BestPracticeSeverity.Should, true,
            "Is the design divided into coherent functional groups with explicit, named interfaces and without hidden cross-coupling?",
            "schematic.context", "visual.artifacts", "design.intent"),
        Rule("SCH-READ-001", "Human-readable schematic", BestPracticeSeverity.Must, true,
            "Can an engineer understand and review signal flow, power, references, stages, values, and intent without first reverse-engineering the netlist?",
            "visual.artifacts", "schematic.context"),
        Rule("PCB-OPAMP-001", "Tight op-amp functional grouping", BestPracticeSeverity.Should, true,
            "Are op-amp feedback, gain-setting, filtering, bias, and decoupling parts placed tightly around the relevant device with short critical loops?",
            "visual.artifacts", "components", "board.summary", "schematic.context"),
        Rule("SIG-BRANCH-001", "Branches, stubs, and reflections", BestPracticeSeverity.Review, false,
            "Have signal branches and stubs been assessed against signal bandwidth, edge rate, trace length, impedance, and possible superimposed harmonics?",
            "board.tracks", "board.nets", "visual.artifacts", "design.intent"),
        Rule("SIG-XTALK-001", "Parallel-route crosstalk", BestPracticeSeverity.Review, false,
            "Are long parallel routes, especially between sensitive or fast nets, avoided or justified using spacing, reference plane, shielding, and coupling length?",
            "board.tracks", "board.nets", "visual.artifacts", "design.intent"),
        Rule("PWR-RETURN-001", "Power and return-path dimensioning", BestPracticeSeverity.Must, false,
            "Are power and ground conductors dimensioned from current, voltage drop, heating, fault behavior, and return-path continuity rather than visual preference alone?",
            "board.tracks", "board.nets", "automated.observations", "design.intent"),
        Rule("GND-STITCH-001", "Ground strategy and stitching", BestPracticeSeverity.Should, true,
            "Does the board have a coherent low-impedance ground/return strategy, including appropriate via stitching where it improves return continuity or shielding?",
            "board.vias", "board.nets", "visual.artifacts", "automated.observations"),
        Rule("DFT-STAGES-001", "Stage-by-stage test access", BestPracticeSeverity.Should, true,
            "Can power, ground, and the input/output of each amplifier and filter stage be measured without unsafe or impractical probing?",
            "schematic.context", "components", "visual.artifacts", "automated.observations", "design.intent"),
        Rule("DFT-CURRENT-001", "Current measurement provision", BestPracticeSeverity.Review, true,
            "Where prototype current consumption matters, is there a practical jumper, link, or optional shunt arrangement for current measurement?",
            "components", "schematic.context", "visual.artifacts", "automated.observations"),
        Rule("PROTO-SIZE-001", "Prototype-friendly component sizes", BestPracticeSeverity.Should, false,
            "For a first prototype intended for manual inspection or rework, are passive packages 0806 or larger unless a documented reason justifies smaller parts?",
            "components", "automated.observations", "design.intent"),
        Rule("VALIDATE-UNCERTAIN-001", "Prototype evidence for uncertain behavior", BestPracticeSeverity.Must, false,
            "Are uncertain functions such as threshold detection, harmonic response, stability, or filter behavior explicitly marked as unproven until suitable simulation or prototype measurements exist?",
            "simulation.tests", "design.intent", "schematic.context"),
        Rule("EMC-CLAIM-001", "Evidence-bounded EMC claims", BestPracticeSeverity.Must, true,
            "Does the review avoid claiming EMC robustness from layout appearance alone, and identify missing analysis or measurements where EMC risk is material?",
            "board.tracks", "board.vias", "visual.artifacts", "design.intent")
    };

    public static IReadOnlyDictionary<string, BestPracticeRule> ById { get; } =
        All.ToDictionary(static rule => rule.Id, StringComparer.Ordinal);

    private static BestPracticeRule Rule(
        string id,
        string title,
        BestPracticeSeverity severity,
        bool requiresVisualEvidence,
        string question,
        params string[] evidenceIds) =>
        new(id, title, severity, requiresVisualEvidence, question, evidenceIds);
}

public static class BestPracticeAssessmentSchema
{
    public static string Create()
    {
        var schema = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "version", "promptVersion", "reviewId", "evidenceHash", "reviewer", "criteria", "unresolvedQuestions", "limitations" },
            properties = new
            {
                version = new { @const = BestPracticeReviewService.AssessmentVersion },
                promptVersion = new { @const = BestPracticeReviewService.PromptVersion },
                reviewId = new { type = "string" },
                evidenceHash = new { type = "string", pattern = "^[0-9a-f]{64}$" },
                reviewer = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "kind", "model" },
                    properties = new { kind = new { type = "string" }, model = new { type = "string" } }
                },
                criteria = new
                {
                    type = "array",
                    minItems = BestPracticeRuleCatalog.All.Count,
                    maxItems = BestPracticeRuleCatalog.All.Count,
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "ruleId", "status", "confidence", "summary", "evidenceIds" },
                        properties = new
                        {
                            ruleId = new { type = "string", @enum = BestPracticeRuleCatalog.All.Select(static rule => rule.Id).ToArray() },
                            status = new { type = "string", @enum = new[] { "pass", "concern", "fail", "notApplicable", "unableToAssess" } },
                            confidence = new { type = "string", @enum = new[] { "high", "medium", "low" } },
                            summary = new { type = "string", minLength = 1 },
                            evidenceIds = new { type = "array", items = new { type = "string" } },
                            recommendation = new { type = new[] { "string", "null" } },
                            applicabilityRationale = new { type = new[] { "string", "null" } }
                        }
                    }
                },
                unresolvedQuestions = new { type = "array", items = new { type = "string" } },
                limitations = new { type = "array", items = new { type = "string" } }
            }
        };
        return JsonSerializer.Serialize(schema, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    }
}

public enum BestPracticeSeverity { Must, Should, Review }
public enum BestPracticeCriterionStatus { Pass, Concern, Fail, NotApplicable, UnableToAssess }
public enum BestPracticeConfidence { High, Medium, Low }
public enum BestPracticeDisposition { Pass, PassWithConcerns, Revise, UnableToAssess }

public sealed record BestPracticeRule(
    string Id,
    string Title,
    BestPracticeSeverity Severity,
    bool RequiresVisualEvidence,
    string Question,
    IReadOnlyList<string> SuggestedEvidenceIds);
public sealed record BestPracticeSourceFile(string Path, string Sha256, long SizeBytes);
public sealed record BestPracticeEvidenceItem(
    string Id,
    string Description,
    bool Available,
    JsonElement Data,
    string? Limitation,
    IReadOnlyList<string> SourceFiles);
public sealed record BestPracticeAutomatedObservation(string Id, string Summary, IReadOnlyList<string> Details);
public sealed record BestPracticeEvidenceBundle(
    int Version,
    string ProjectRoot,
    IReadOnlyList<BestPracticeSourceFile> SourceFiles,
    IReadOnlyList<BestPracticeEvidenceItem> Items);
public sealed record BestPracticeReviewPreparation(
    int PromptVersion,
    string ReviewId,
    string EvidenceHash,
    string ProjectRoot,
    IReadOnlyList<BestPracticeRule> Rules,
    BestPracticeEvidenceBundle Evidence,
    string Prompt,
    string AssessmentJsonSchema);
public sealed record BestPracticeReviewer(string Kind, string Model);
public sealed record BestPracticeCriterionAssessment(
    string RuleId,
    BestPracticeCriterionStatus Status,
    BestPracticeConfidence Confidence,
    string Summary,
    IReadOnlyList<string> EvidenceIds,
    string? Recommendation = null,
    string? ApplicabilityRationale = null);
public sealed record BestPracticeAssessment(
    int Version,
    int PromptVersion,
    string ReviewId,
    string EvidenceHash,
    BestPracticeReviewer Reviewer,
    IReadOnlyList<BestPracticeCriterionAssessment> Criteria,
    IReadOnlyList<string> UnresolvedQuestions,
    IReadOnlyList<string> Limitations);
public sealed record BestPracticeReviewFinding(
    string RuleId,
    string Title,
    BestPracticeSeverity Severity,
    BestPracticeCriterionStatus Status,
    BestPracticeConfidence Confidence,
    string Summary,
    IReadOnlyList<string> EvidenceIds,
    string? Recommendation);
public sealed record BestPracticeReviewReport(
    int Version,
    string RunId,
    DateTimeOffset GeneratedAtUtc,
    string ProjectRoot,
    string EvidenceHash,
    int PromptVersion,
    BestPracticeReviewer Reviewer,
    BestPracticeDisposition Disposition,
    IReadOnlyList<BestPracticeReviewFinding> Findings,
    IReadOnlyList<string> UnresolvedQuestions,
    IReadOnlyList<string> Limitations,
    string ReportPath,
    string MarkdownPath,
    IReadOnlyList<string> ArtifactPaths);
public sealed record BestPracticeReviewStatus(
    string RunId,
    string ReportEvidenceHash,
    string CurrentEvidenceHash,
    bool EvidenceCurrent,
    BestPracticeDisposition Disposition,
    bool Passed,
    string ReportPath);
