using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCBHelper.Core;

public sealed class DesignPlanService
{
    private static readonly IReadOnlySet<string> SupportedOperations = DesignPlanOperationCatalog.ByType.Keys.ToHashSet(StringComparer.Ordinal);

    private readonly ProjectDiscoveryService _projects;
    private readonly ProjectTransactionService _transactions;
    private readonly EngineeringGateService _gates;
    private static readonly IReadOnlyDictionary<string, IPlanOperationHandler> OperationHandlers = SupportedOperations
        .ToDictionary(static type => type, static type => (IPlanOperationHandler)new BuiltInPlanOperationHandler(type), StringComparer.Ordinal);

    public DesignPlanService(ProjectDiscoveryService projects, ProjectTransactionService transactions, EngineeringGateService gates)
    {
        _projects = projects;
        _transactions = transactions;
        _gates = gates;
    }

    public ToolResponse<DesignPlanValidationResult> Validate(string projectPath, string json)
    {
        var project = _projects.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<DesignPlanValidationResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        var parsed = Parse(json);
        if (!parsed.Success || parsed.Data is null)
        {
            return ToolResponse<DesignPlanValidationResult>.Fail(parsed.Summary, parsed.Error?.Code ?? "PLAN_INVALID", parsed.Error?.Message);
        }

        var canonical = Canonicalize(parsed.Data.Root);
        return ToolResponse<DesignPlanValidationResult>.Ok(
            $"Validated design plan with {parsed.Data.Operations.Count} operation(s).",
            new DesignPlanValidationResult(Hash(canonical), canonical, parsed.Data.Goal, parsed.Data.Operations.Count));
    }

    public ToolResponse<DesignPlanPreviewResult> Preview(string projectPath, string json)
    {
        var project = _projects.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<DesignPlanPreviewResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        var parsed = Parse(json);
        if (!parsed.Success || parsed.Data is null)
        {
            return ToolResponse<DesignPlanPreviewResult>.Fail(parsed.Summary, parsed.Error?.Code ?? "PLAN_INVALID", parsed.Error?.Message);
        }

        var canonical = Canonicalize(parsed.Data.Root);
        var planHash = Hash(canonical);
        var prepared = PrepareInSandbox(project.Data, parsed.Data);
        if (!prepared.Success || prepared.Data is null)
        {
            return ToolResponse<DesignPlanPreviewResult>.Fail(prepared.Summary, "PLAN_PREPARATION_FAILED", prepared.Error?.Message ?? prepared.Summary);
        }

        var risk = PlanRiskEvaluator.Evaluate(planHash, parsed.Data);
        var result = new DesignPlanPreviewResult(
            planHash,
            canonical,
            prepared.Data.Operations,
            prepared.Data.Changes.Select(static change => new PreparedFileSummary(change.RelativePath, change.BeforeHash, change.AfterHash)).ToArray(),
            risk.Risk,
            risk.Decisions,
            parsed.Data.EngineeringGate,
            prepared.Data.Warnings);
        return ToolResponse<DesignPlanPreviewResult>.Ok(
            $"Prepared {prepared.Data.Operations.Count} operation(s) affecting {prepared.Data.Changes.Count} file(s).",
            result,
            prepared.Data.Warnings);
    }

    public async Task<ToolResponse<DesignPlanApplyResult>> ApplyAsync(
        string projectPath,
        string json,
        string expectedPlanHash,
        IReadOnlyList<string>? acknowledgedDecisionIds = null,
        CancellationToken cancellationToken = default)
    {
        var project = _projects.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<DesignPlanApplyResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        var parsed = Parse(json);
        if (!parsed.Success || parsed.Data is null)
        {
            return ToolResponse<DesignPlanApplyResult>.Fail(parsed.Summary, parsed.Error?.Code ?? "PLAN_INVALID", parsed.Error?.Message);
        }

        var canonical = Canonicalize(parsed.Data.Root);
        var planHash = Hash(canonical);
        if (!string.Equals(planHash, expectedPlanHash, StringComparison.OrdinalIgnoreCase))
        {
            return ToolResponse<DesignPlanApplyResult>.Fail("The expected plan hash does not match the supplied plan.", "PLAN_HASH_MISMATCH");
        }

        var risk = PlanRiskEvaluator.Evaluate(planHash, parsed.Data);
        if (risk.Risk == PlanRisk.Blocked)
        {
            return ToolResponse<DesignPlanApplyResult>.Fail("The design plan is blocked by policy.", "PLAN_BLOCKED");
        }

        var acknowledgements = acknowledgedDecisionIds ?? Array.Empty<string>();
        var missing = risk.Decisions.Where(decision => !acknowledgements.Contains(decision.DecisionId, StringComparer.Ordinal)).ToArray();
        if (missing.Length > 0)
        {
            return ToolResponse<DesignPlanApplyResult>.Fail("The design plan requires unresolved user decisions.", "PLAN_DECISION_REQUIRED");
        }

        var prepared = PrepareInSandbox(project.Data, parsed.Data);
        if (!prepared.Success || prepared.Data is null)
        {
            return ToolResponse<DesignPlanApplyResult>.Fail(prepared.Summary, "PLAN_PREPARATION_FAILED", prepared.Error?.Message ?? prepared.Summary);
        }

        var applied = await _transactions.ApplyAsync(project.Data.ProjectRoot, parsed.Data.Goal, planHash,
            prepared.Data.Operations, prepared.Data.Changes, acknowledgements, cancellationToken);
        if (!applied.Success || applied.Data is null)
        {
            return ToolResponse<DesignPlanApplyResult>.Fail(applied.Summary, applied.Error?.Code ?? "TRANSACTION_APPLY_FAILED", applied.Error?.Message);
        }

        var gate = await _gates.RunAsync(project.Data.ProjectRoot, parsed.Data.EngineeringGate, cancellationToken);
        if (!gate.Success || gate.Data is null || gate.Data.Status == EngineeringGateStatus.ExecutionFailed)
        {
            var restored = await _transactions.RestoreAsync(project.Data.ProjectRoot, applied.Data.Transaction.TransactionId, CancellationToken.None);
            return ToolResponse<DesignPlanApplyResult>.Fail(
                restored.Success ? "Engineering gate execution failed; transaction was rolled back." : "Engineering gate execution failed and rollback was incomplete.",
                restored.Success ? "ENGINEERING_GATE_EXECUTION_FAILED" : "TRANSACTION_INCOMPLETE",
                gate.Error?.Message ?? gate.Summary);
        }

        var recorded = await _transactions.SetGateResultAsync(project.Data.ProjectRoot, applied.Data.Transaction.TransactionId, gate.Data, cancellationToken);
        var transaction = recorded.Data ?? applied.Data;
        return ToolResponse<DesignPlanApplyResult>.Ok(
            gate.Data.Status == EngineeringGateStatus.Passed
                ? $"Applied plan and passed engineering gates in transaction {transaction.Transaction.TransactionId}."
                : $"Applied plan; engineering gate status is {gate.Data.Status}.",
            new DesignPlanApplyResult(planHash, transaction, gate.Data),
            prepared.Data.Warnings);
    }

    private static ToolResponse<ParsedDesignPlan> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid("Design plan must be a JSON object.");
            }

            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1)
            {
                return root.TryGetProperty("version", out _)
                    ? ToolResponse<ParsedDesignPlan>.Fail("Only Design Plan version 1 is supported.", "PLAN_VERSION_UNSUPPORTED")
                    : Invalid("Design plan version is required.");
            }

            if (!root.TryGetProperty("goal", out var goalElement) || goalElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(goalElement.GetString()))
            {
                return Invalid("Design plan goal is required.");
            }

            if (!root.TryGetProperty("operations", out var operationsElement) || operationsElement.ValueKind != JsonValueKind.Array || operationsElement.GetArrayLength() == 0)
            {
                return Invalid("Design plan requires at least one operation.");
            }

            var operations = new List<PlanOperation>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in operationsElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String
                    || !element.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                {
                    return Invalid("Every operation requires string id and type properties.");
                }

                var id = idElement.GetString()!;
                var type = typeElement.GetString()!;
                if (string.IsNullOrWhiteSpace(id) || !ids.Add(id))
                {
                    return ToolResponse<ParsedDesignPlan>.Fail($"Duplicate operation id: {id}", "PLAN_OPERATION_ID_DUPLICATE");
                }

                if (!SupportedOperations.Contains(type))
                {
                    return ToolResponse<ParsedDesignPlan>.Fail($"Unsupported operation: {type}", "PLAN_OPERATION_UNSUPPORTED");
                }

                var propertyError = DesignPlanOperationCatalog.ValidateProperties(type, element);
                if (propertyError is not null)
                {
                    return ToolResponse<ParsedDesignPlan>.Fail(propertyError, "PLAN_INVALID");
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Contains("path", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("command", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("script", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("raw", StringComparison.OrdinalIgnoreCase))
                    {
                        return ToolResponse<ParsedDesignPlan>.Fail($"Operation property is forbidden: {property.Name}", "PLAN_INVALID");
                    }
                }

                operations.Add(new PlanOperation(id, type, element.Clone()));
            }

            var gate = EngineeringGateRequirements.Default;
            if (root.TryGetProperty("engineeringGate", out var gateElement))
            {
                if (gateElement.ValueKind != JsonValueKind.Object)
                {
                    return Invalid("engineeringGate must be an object.");
                }

                gate = new EngineeringGateRequirements(
                    String(gateElement, "erc", "required"),
                    String(gateElement, "drc", "required"),
                    String(gateElement, "manufacturingValidation", "required"),
                    String(gateElement, "simulationAssertions", String(gateElement, "simulation", "skip")),
                    String(gateElement, "designIntent", "optional"));
                if (!EngineeringGateService.IsValidRequirement(gate.Erc)
                    || !EngineeringGateService.IsValidRequirement(gate.Drc)
                    || !EngineeringGateService.IsValidRequirement(gate.ManufacturingValidation)
                    || !EngineeringGateService.IsValidRequirement(gate.Simulation)
                    || !EngineeringGateService.IsValidRequirement(gate.DesignIntent))
                {
                    return Invalid("Engineering gate values must be required, optional, or skip.");
                }
            }

            return ToolResponse<ParsedDesignPlan>.Ok("Parsed design plan.", new ParsedDesignPlan(root.Clone(), goalElement.GetString()!, operations, gate));
        }
        catch (JsonException exception)
        {
            return ToolResponse<ParsedDesignPlan>.Fail("Design plan JSON is invalid.", "PLAN_INVALID", exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return ToolResponse<ParsedDesignPlan>.Fail("Design plan contains an invalid value.", "PLAN_INVALID", exception.Message);
        }
    }

    private ToolResponse<PlanPreparation> PrepareInSandbox(ProjectSummary project, ParsedDesignPlan plan)
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "pcbhelper-plan", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(sandbox);
            foreach (var file in Directory.GetFiles(project.ProjectRoot, "*", SearchOption.TopDirectoryOnly))
            {
                File.Copy(file, Path.Combine(sandbox, Path.GetFileName(file)));
            }
            var intentSource = Path.Combine(project.ProjectRoot, ".pcbhelper", "design-intent.json");
            if (File.Exists(intentSource))
            {
                var intentTarget = Path.Combine(sandbox, ".pcbhelper", "design-intent.json");
                Directory.CreateDirectory(Path.GetDirectoryName(intentTarget)!);
                File.Copy(intentSource, intentTarget);
            }

            var before = CaptureDesignFiles(project.ProjectRoot);
            var sandboxProjects = new ProjectDiscoveryService(ProjectScopePolicy.Unrestricted());
            var component = new ComponentService(sandboxProjects);
            var geometry = new GeometryService(sandboxProjects);
            var schematic = new SchematicAuthoringService(sandboxProjects);
            var routing = new RoutingService(sandboxProjects);
            var finishing = new BoardFinishingService(sandboxProjects);
            var designIntent = new DesignIntentService(sandboxProjects, new BoardInspectionService(sandboxProjects));
            var preparedOperations = new List<PreparedOperation>();
            var warnings = new List<string>();
            foreach (var operation in plan.Operations)
            {
                var result = Execute(operation, sandbox, component, geometry, schematic, routing, finishing, designIntent);
                if (!result.Success)
                {
                    return ToolResponse<PlanPreparation>.Fail(
                        $"Operation {operation.Id} ({operation.Type}) could not be prepared: {result.Summary}",
                        "PLAN_PREPARATION_FAILED",
                        result.Error?.Message ?? result.Summary);
                }

                preparedOperations.Add(new PreparedOperation(operation.Id, operation.Type, result.Summary));
                warnings.AddRange(result.Warnings);
            }

            var after = CaptureDesignFiles(sandbox);
            var names = before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
            var changes = names
                .Where(name => !string.Equals(before.GetValueOrDefault(name), after.GetValueOrDefault(name), StringComparison.Ordinal))
                .Select(name => PreparedFileChange.Create(name, before.GetValueOrDefault(name), after.GetValueOrDefault(name)))
                .ToArray();
            if (changes.Length == 0)
            {
                return ToolResponse<PlanPreparation>.Fail("Plan operations produced no file changes.", "PLAN_PREPARATION_FAILED");
            }

            return ToolResponse<PlanPreparation>.Ok("Prepared design plan.", new PlanPreparation(preparedOperations, changes, warnings));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            return ToolResponse<PlanPreparation>.Fail("Could not prepare design plan.", "PLAN_PREPARATION_FAILED", exception.Message);
        }
        finally
        {
            if (Directory.Exists(sandbox))
            {
                try { Directory.Delete(sandbox, true); } catch (IOException) { }
            }
        }
    }

    private static ToolResponse<object> Execute(
        PlanOperation operation, string projectPath, ComponentService component, GeometryService geometry,
        SchematicAuthoringService schematic, RoutingService routing, BoardFinishingService finishing, DesignIntentService designIntent)
    {
        if (!OperationHandlers.TryGetValue(operation.Type, out var handler))
            return ToolResponse<object>.Fail($"Unsupported operation: {operation.Type}", "PLAN_OPERATION_UNSUPPORTED");
        var prepared = handler.Prepare(operation, new PlanPreparationContext(projectPath, component, geometry, schematic, routing, finishing, designIntent));
        return prepared.Success
            ? ToolResponse<object>.Ok(prepared.Summary, prepared.Data!, prepared.Warnings)
            : ToolResponse<object>.Fail(prepared.Summary, prepared.Error?.Code ?? "PLAN_PREPARATION_FAILED", prepared.Error?.Message);
    }

    private static ToolResponse<object> ExecutePrimitive(PlanOperation operation, PlanPreparationContext context)
    {
        var p = operation.Properties;
        return operation.Type switch
        {
            "set-component-value" => Box(context.Components.SetValue(context.ProjectPath, RequiredString(p, "reference"), RequiredString(p, "value"), String(p, "scope", "available"), false)),
            "set-design-intent" => Box(context.DesignIntent.SetIntent(context.ProjectPath, p.GetProperty("intent"), false)),
            "move-component" => Box(context.Geometry.MoveComponent(context.ProjectPath, RequiredString(p, "reference"), RequiredDouble(p, "xMm"), RequiredDouble(p, "yMm"), false)),
            "set-component-spacing" => Box(context.Geometry.SetComponentSpacing(context.ProjectPath, RequiredString(p, "fixedReference"), RequiredString(p, "movingReference"), RequiredDouble(p, "distanceMm"), String(p, "axis", "x"), false)),
            "create-schematic-symbol" => Box(context.Schematic.CreateSymbol(context.ProjectPath, RequiredString(p, "symbol"), RequiredString(p, "reference"), RequiredDouble(p, "xMm"), RequiredDouble(p, "yMm"), OptionalString(p, "value"), OptionalString(p, "footprint"), OptionalInt(p, "unit", 1), false)),
            "set-symbol-field" => Box(context.Schematic.SetSymbolField(context.ProjectPath, RequiredString(p, "reference"), RequiredString(p, "field"), RequiredString(p, "value"), false)),
            "connect-schematic-pins" => Box(context.Schematic.ConnectPins(context.ProjectPath, RequiredString(p, "from"), RequiredString(p, "to"), OptionalString(p, "net"), false)),
            "add-net-label" => Box(context.Schematic.AddNetLabel(context.ProjectPath, RequiredString(p, "net"), RequiredDouble(p, "xMm"), RequiredDouble(p, "yMm"), false)),
            "update-pcb-from-schematic" => Box(context.Schematic.UpdatePcbFromSchematic(context.ProjectPath, false)),
            "regenerate-board-footprint" => Box(context.Schematic.RegenerateBoardFootprint(context.ProjectPath, RequiredString(p, "reference"), false)),
            "add-track" => Box(context.Routing.AddTrack(context.ProjectPath, RequiredString(p, "net"), RequiredDouble(p, "startXmm"), RequiredDouble(p, "startYmm"), RequiredDouble(p, "endXmm"), RequiredDouble(p, "endYmm"), RequiredString(p, "layer"), RequiredDouble(p, "widthMm"), false)),
            "add-track-polyline" => Box(context.Routing.AddTrackPolyline(context.ProjectPath, RequiredString(p, "net"), RequiredString(p, "points"), RequiredString(p, "layer"), RequiredDouble(p, "widthMm"), false)),
            "delete-track" => Box(context.Routing.DeleteTrack(context.ProjectPath, RequiredString(p, "track"), false)),
            "add-via" => Box(context.Routing.AddVia(context.ProjectPath, RequiredString(p, "net"), RequiredDouble(p, "xMm"), RequiredDouble(p, "yMm"), RequiredDouble(p, "sizeMm"), RequiredDouble(p, "drillMm"), String(p, "layers", "F.Cu,B.Cu"), false)),
            "delete-via" => Box(context.Routing.DeleteVia(context.ProjectPath, RequiredString(p, "via"), false)),
            "add-copper-zone" => Box(context.Finishing.AddCopperZone(context.ProjectPath, RequiredString(p, "net"), RequiredString(p, "layer"), RequiredString(p, "points"), RequiredDouble(p, "clearanceMm"), RequiredDouble(p, "minThicknessMm"), false)),
            "update-copper-zone" => Box(context.Finishing.UpdateCopperZone(context.ProjectPath, RequiredString(p, "zone"), OptionalString(p, "net"), OptionalString(p, "layer"), OptionalString(p, "points"), false)),
            "move-reference-text" => Box(context.Finishing.MoveReferenceText(context.ProjectPath, RequiredString(p, "reference"), RequiredDouble(p, "xMm"), RequiredDouble(p, "yMm"), false)),
            "hide-reference-text" => Box(context.Finishing.HideReferenceText(context.ProjectPath, RequiredString(p, "reference"), false)),
            "cleanup-silkscreen" => Box(context.Finishing.CleanupSilkscreen(context.ProjectPath, RequiredDouble(p, "minimumSpacingMm"), false)),
            "add-testpoint" => Box(context.Finishing.AddTestPoint(context.ProjectPath, RequiredString(p, "reference"), RequiredString(p, "net"), RequiredDouble(p, "xMm"), RequiredDouble(p, "yMm"), RequiredDouble(p, "diameterMm"), false)),
            "add-mounting-hole" => Box(context.Finishing.AddMountingHole(context.ProjectPath, RequiredString(p, "reference"), RequiredDouble(p, "xMm"), RequiredDouble(p, "yMm"), RequiredDouble(p, "drillMm"), RequiredDouble(p, "diameterMm"), false)),
            "add-mechanical-keepout" => Box(context.Finishing.AddMechanicalKeepout(context.ProjectPath, RequiredString(p, "layer"), RequiredString(p, "points"), false)),
            _ => ToolResponse<object>.Fail($"Unsupported operation: {operation.Type}", "PLAN_OPERATION_UNSUPPORTED")
        };
    }

    private sealed class BuiltInPlanOperationHandler : IPlanOperationHandler
    {
        public BuiltInPlanOperationHandler(string operationType) => OperationType = operationType;
        public string OperationType { get; }
        public ToolResponse<PreparedOperation> Prepare(PlanOperation operation, PlanPreparationContext context)
        {
            var response = ExecutePrimitive(operation, context);
            return response.Success
                ? ToolResponse<PreparedOperation>.Ok(response.Summary, new PreparedOperation(operation.Id, operation.Type, response.Summary), response.Warnings)
                : ToolResponse<PreparedOperation>.Fail(response.Summary, response.Error?.Code ?? "PLAN_PREPARATION_FAILED", response.Error?.Message);
        }
    }

    private static ToolResponse<object> Box<T>(ToolResponse<T> response) => response.Success
        ? ToolResponse<object>.Ok(response.Summary, response.Data!, response.Warnings)
        : ToolResponse<object>.Fail(response.Summary, response.Error?.Code ?? "PLAN_PREPARATION_FAILED", response.Error?.Message);

    private static Dictionary<string, string> CaptureDesignFiles(string root)
    {
        var files = Directory.GetFiles(root, "*.kicad_*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path) is ".kicad_pro" or ".kicad_sch" or ".kicad_pcb")
            .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllText, StringComparer.OrdinalIgnoreCase);
        var intent = Path.Combine(root, ".pcbhelper", "design-intent.json");
        if (File.Exists(intent)) files[Path.GetRelativePath(root, intent)] = File.ReadAllText(intent);
        return files;
    }

    private static string Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) { WriteCanonical(writer, element); }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray(); break;
            case JsonValueKind.String: writer.WriteStringValue(element.GetString()); break;
            case JsonValueKind.Number: writer.WriteRawValue(element.GetRawText()); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new InvalidOperationException("Unsupported JSON value.");
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static ToolResponse<ParsedDesignPlan> Invalid(string message) => ToolResponse<ParsedDesignPlan>.Fail(message, "PLAN_INVALID");
    private static string RequiredString(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()) ? p.GetString()! : throw new InvalidOperationException($"{name} is required.");
    private static string String(JsonElement e, string name, string fallback) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : fallback;
    private static string? OptionalString(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static double RequiredDouble(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.TryGetDouble(out var value) && double.IsFinite(value) ? value : throw new InvalidOperationException($"{name} is required and must be finite.");
    private static int OptionalInt(JsonElement e, string name, int fallback) => e.TryGetProperty(name, out var p) && p.TryGetInt32(out var value) ? value : fallback;
}

public static class PlanRiskEvaluator
{
    public static PlanRiskResult Evaluate(string planHash, ParsedDesignPlan plan)
    {
        var blockedTerms = new[] { "mains", "high-current", "high current", "high-speed", "high speed", "medical", "safety-critical", "safety critical", "radio frequency", " rf " };
        var searchable = $" {plan.Goal} {plan.Root.GetRawText()} ";
        if (blockedTerms.Any(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            var id = DecisionId(planHash, "unsupported-domain");
            return new PlanRiskResult(PlanRisk.Blocked,
                new[] { new PlanDecision(id, "unsupported-domain", "The plan enters a domain outside the small, simple V1 board policy.") });
        }

        var gateOverride = plan.EngineeringGate.Erc != "required"
            || plan.EngineeringGate.Drc != "required"
            || plan.EngineeringGate.ManufacturingValidation != "required";
        if (gateOverride)
        {
            var id = DecisionId(planHash, "engineering-gate-override");
            return new PlanRiskResult(PlanRisk.UserRequired,
                new[] { new PlanDecision(id, "engineering-gate-override", "One or more default release gates are not required.") });
        }

        return new PlanRiskResult(PlanRisk.Automatic, Array.Empty<PlanDecision>());
    }

    private static string DecisionId(string planHash, string kind) =>
        $"decision-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{planHash}:{kind}"))).ToLowerInvariant()[..16]}";
}

public enum PlanRisk { Automatic, UserRequired, Blocked }
public sealed record PlanDecision(string DecisionId, string Kind, string Summary);
public sealed record PlanRiskResult(PlanRisk Risk, IReadOnlyList<PlanDecision> Decisions);
public sealed record PlanOperation(string Id, string Type, JsonElement Properties);
public sealed record ParsedDesignPlan(JsonElement Root, string Goal, IReadOnlyList<PlanOperation> Operations, EngineeringGateRequirements EngineeringGate);
public sealed record PlanPreparation(IReadOnlyList<PreparedOperation> Operations, IReadOnlyList<PreparedFileChange> Changes, IReadOnlyList<string> Warnings);
public sealed record DesignPlanValidationResult(string PlanHash, string NormalizedPlan, string Goal, int OperationCount);
public sealed record PreparedFileSummary(string RelativePath, string BeforeHash, string AfterHash);
public sealed record DesignPlanPreviewResult(string PlanHash, string NormalizedPlan, IReadOnlyList<PreparedOperation> Operations, IReadOnlyList<PreparedFileSummary> ChangedFiles, PlanRisk Risk, IReadOnlyList<PlanDecision> RequiredDecisions, EngineeringGateRequirements EngineeringGate, IReadOnlyList<string> Warnings);
public sealed record DesignPlanApplyResult(string PlanHash, ProjectTransactionResult Transaction, EngineeringGateResult EngineeringGate);
public interface IPlanOperationHandler
{
    string OperationType { get; }
    ToolResponse<PreparedOperation> Prepare(PlanOperation operation, PlanPreparationContext context);
}
public sealed record PlanPreparationContext(string ProjectPath, ComponentService Components, GeometryService Geometry, SchematicAuthoringService Schematic, RoutingService Routing, BoardFinishingService Finishing, DesignIntentService DesignIntent);
