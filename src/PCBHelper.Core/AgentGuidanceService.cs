using System.Reflection;
using System.Text.Json;

namespace PCBHelper.Core;

public sealed class AgentGuidanceService
{
    public const int GuideVersion = 1;
    public const int CapabilityVersion = 6;
    public const string GuideUri = "pcbhelper://agent-guide/v1";
    public const string DesignPlanSchemaUri = "pcbhelper://design-plan/v1/schema";

    public AgentGuideResult GetGuide() => new(GuideVersion, GuideUri, ReadEmbeddedGuide(), AgentPolicyRules.All);

    public ServerCapabilitiesResult GetCapabilities(string profile) => new(
        CapabilityVersion,
        typeof(AgentGuidanceService).Assembly.GetName().Version?.ToString() ?? "unknown",
        profile,
        GuideVersion,
        GuideUri,
        1,
        DesignPlanSchemaUri,
        DesignPlanOperationCatalog.All,
        SchematicSymbolCatalog.All.Select(static entry => new ApprovedSchematicSymbol(
            entry.SymbolId,
            entry.DefaultValue,
            entry.DefaultFootprint,
            entry.Units,
            entry.Source)).ToArray(),
        new[] { "Approved blank two-layer project bootstrap", "Small, simple, reversible two-layer PCB workflows", "Transactional Design Plan mutation", "Native KiCad 10 design block catalog with provenance, interface contracts, immutable preview hashes, and evidence-gated maturity", "Evidence-bound qualitative best-practice review using a pure versioned LLM prompt and stale-design rejection", "Hash-bound layout constraint proofs with unavailable evidence kept distinct from passing", "Content-addressed workflow artifact inspection without recomputing release disposition", "ERC, DRC, simulation, manufacturing review, policy-driven release audit, and PCBWay package generation" },
        new[] { "No arbitrary KiCad text, shell commands, or file operations in Design Plans", "No general autorouting, safety-critical, mains, RF, high-current, or high-speed design", "No order placement, payment, or component substitution approval" });

    public string GetDesignPlanSchema() => DesignPlanOperationCatalog.CreateJsonSchema();

    private static string ReadEmbeddedGuide()
    {
        var assembly = typeof(AgentGuidanceService).Assembly;
        var name = assembly.GetManifestResourceNames().Single(static name => name.EndsWith("agent-guide-v1.md", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("Embedded agent guide is unavailable.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public static class AgentPolicyRules
{
    public static readonly IReadOnlyList<AgentPolicyRule> All = new[]
    {
        new AgentPolicyRule("USE_WORKFLOW_PROFILE", "Use the workflow MCP profile and declarative Design Plans for normal work."),
        new AgentPolicyRule("CONTEXT_BEFORE_PLAN", "Read capabilities and project context before proposing a plan."),
        new AgentPolicyRule("PREVIEW_HASH_REQUIRED", "Validate and preview before applying the exact returned plan hash."),
        new AgentPolicyRule("NO_RAW_KICAD_OR_SHELL", "Do not bypass supported operations with raw KiCad edits or commands."),
        new AgentPolicyRule("NO_GUI_AS_MUTATION_FALLBACK", "Do not use GUI automation as the normal design mutation path."),
        new AgentPolicyRule("GATES_NOT_JUDGMENT", "Engineering gates are evidence and do not replace engineering judgment."),
        new AgentPolicyRule("SIMULATION_EVIDENCE_REQUIRED_FOR_FUNCTION", "Do not claim electrical function without suitable simulation or physical evidence."),
        new AgentPolicyRule("NO_ASSERTION_WEAKENING", "Do not weaken assertions merely to make a design pass."),
        new AgentPolicyRule("NO_STALE_EXPORTS", "Regenerate release outputs after the final design mutation."),
        new AgentPolicyRule("BLOCKS_REQUIRE_PROVENANCE", "Import or create design blocks only with explicit source, license, attribution, redistribution status, and immutable payload hashes."),
        new AgentPolicyRule("BLOCK_MATURITY_REQUIRES_EVIDENCE", "Do not increase design-block maturity or publish a block without the required review, simulation, bench, or production evidence."),
        new AgentPolicyRule("NO_SILENT_BLOCK_UPGRADES", "Never overwrite or silently upgrade a design block; create a reviewed semantic version and preview its exact hash."),
        new AgentPolicyRule("BEST_PRACTICE_REVIEW_REQUIRES_EVIDENCE", "For qualitative best-practice review, prepare the versioned prompt, inspect the cited evidence, and submit every rule against the exact evidence hash."),
        new AgentPolicyRule("LLM_REVIEW_IS_NOT_PROOF", "Treat best-practice LLM findings as structured engineering judgment, not proof of electrical function, EMC, safety, or manufacturability."),
        new AgentPolicyRule("VISUAL_CLAIMS_REQUIRE_VISUAL_INSPECTION", "Do not pass visual schematic or PCB criteria merely because render paths exist; inspect the artifacts or mark the criterion unable to assess."),
        new AgentPolicyRule("UNAVAILABLE_CONSTRAINTS_NOT_PASS", "Treat unresolved nets, missing routed segments, and unfilled zones as unavailable layout evidence, never as passing constraints."),
        new AgentPolicyRule("PCBWAY_REQUIRES_GERBER_BOM_CPL", "Assembly release requires current Gerber, BOM, and CPL outputs."),
        new AgentPolicyRule("NO_FALSE_GUI_REFRESH", "Do not report a GUI refresh when only project files changed."),
        new AgentPolicyRule("NO_ORDER_OR_PAYMENT", "Never place an order, pay, or approve substitutions without the user."),
        new AgentPolicyRule("ASK_ONLY_FOR_EXCEPTION_DECISIONS", "Ask only for material ambiguity, risk, cost, policy, or irreversible external decisions.")
    };
}

public sealed record AgentPolicyRule(string Id, string Instruction);
public sealed record AgentGuideResult(int GuideVersion, string Uri, string Markdown, IReadOnlyList<AgentPolicyRule> PolicyRules);
public sealed record ServerCapabilitiesResult(int CapabilityVersion, string ServerVersion, string Profile, int AgentGuideVersion, string AgentGuideUri,
    int DesignPlanVersion, string DesignPlanSchemaUri, IReadOnlyList<DesignPlanOperationDefinition> Operations,
    IReadOnlyList<ApprovedSchematicSymbol> ApprovedSymbols,
    IReadOnlyList<string> Capabilities, IReadOnlyList<string> Limitations);
public sealed record ApprovedSchematicSymbol(string SymbolId, string DefaultValue, string DefaultFootprint, IReadOnlyList<int> Units, string Source);

public enum DesignPlanPropertyKind { String, Number, Integer, Object }
public sealed record DesignPlanPropertyDefinition(string Name, DesignPlanPropertyKind Kind, bool Required, object? DefaultValue = null);
public sealed record DesignPlanOperationDefinition(string Type, string Description, IReadOnlyList<DesignPlanPropertyDefinition> Properties, bool Reversible = true);

public static class DesignPlanOperationCatalog
{
    private static DesignPlanPropertyDefinition S(string name, bool required = true, string? fallback = null) => new(name, DesignPlanPropertyKind.String, required, fallback);
    private static DesignPlanPropertyDefinition N(string name) => new(name, DesignPlanPropertyKind.Number, true);
    private static DesignPlanPropertyDefinition I(string name, int fallback) => new(name, DesignPlanPropertyKind.Integer, false, fallback);
    private static DesignPlanPropertyDefinition O(string name) => new(name, DesignPlanPropertyKind.Object, true);

    public static readonly IReadOnlyList<DesignPlanOperationDefinition> All = new[]
    {
        Op("set-component-value", "Set a component value in available design files.", S("reference"), S("value"), S("scope", false, "available")),
        Op("set-design-intent", "Set the structured, project-scoped design intent used by deterministic verification.", O("intent")),
        Op("set-simulation-fixture", "Compile a structured, project-contained ngspice fixture and its declarative assertions.", O("fixture")),
        Op("move-component", "Move one board footprint.", S("reference"), N("xMm"), N("yMm")),
        Op("rotate-component", "Rotate one board footprint around its origin.", S("reference"), N("rotationDegrees")),
        Op("set-component-spacing", "Set axis-limited spacing between footprints.", S("fixedReference"), S("movingReference"), N("distanceMm"), S("axis", false, "x")),
        Op("create-schematic-symbol", "Place an approved schematic symbol.", S("symbol"), S("reference"), N("xMm"), N("yMm"), S("value", false), S("footprint", false), I("unit", 1)),
        Op("delete-schematic-symbol", "Delete every schematic unit with one reference while leaving surrounding wires unchanged.", S("reference")),
        Op("replace-schematic-symbol", "Replace an approved schematic symbol while preserving placement, fields, instances, and compatible pin wiring.", S("reference"), S("symbol")),
        Op("set-symbol-field", "Set one schematic symbol field.", S("reference"), S("field"), S("value")),
        Op("hide-symbol-field", "Hide one schematic symbol field while preserving its value for sourcing and traceability.", S("reference"), S("field")),
        Op("connect-schematic-pins", "Connect two approved symbol pins.", S("from"), S("to"), S("net", false)),
        Op("arrange-schematic", "Deterministically arrange and route a complete supported schematic while preserving its canonical pin/net partition."),
        Op("delete-schematic-wire-by-uuid", "Delete one exact schematic wire by UUID.", S("uuid")),
        Op("delete-schematic-wire", "Delete one exact schematic wire by endpoint coordinates.", N("x1Mm"), N("y1Mm"), N("x2Mm"), N("y2Mm"), N("toleranceMm")),
        Op("delete-net-label-by-uuid", "Delete one exact schematic net label by UUID.", S("uuid")),
        Op("add-net-label", "Add a schematic net label.", S("net"), N("xMm"), N("yMm")),
        Op("add-schematic-block-box", "Add a titled non-filled box that makes a functional schematic block explicit to human reviewers.", S("title"), N("xMm"), N("yMm"), N("widthMm"), N("heightMm")),
        Op("replace-net-label", "Replace one schematic net label at an exact location.", S("currentNet"), S("newNet"), N("xMm"), N("yMm"), N("toleranceMm")),
        Op("update-pcb-from-schematic", "Create missing template footprints and board nets."),
        Op("regenerate-board-footprint", "Regenerate one template footprint from schematic data.", S("reference")),
        Op("add-track", "Add one straight copper segment.", S("net"), N("startXmm"), N("startYmm"), N("endXmm"), N("endYmm"), S("layer"), N("widthMm")),
        Op("add-track-polyline", "Add straight segments along a point list.", S("net"), S("points"), S("layer"), N("widthMm")),
        Op("delete-track", "Delete one track by identifier.", S("track")),
        Op("add-via", "Add one through via.", S("net"), N("xMm"), N("yMm"), N("sizeMm"), N("drillMm"), S("layers", false, "F.Cu,B.Cu")),
        Op("delete-via", "Delete one via by identifier.", S("via"))
        ,Op("add-copper-zone", "Add an unfilled copper zone polygon.", S("net"), S("layer"), S("points"), N("clearanceMm"), N("minThicknessMm"))
        ,Op("refill-zones", "Refill and save every copper zone with KiCad Python.")
        ,Op("update-copper-zone", "Update an existing copper zone by UUID.", S("zone"), S("net", false), S("layer", false), S("points", false))
        ,Op("move-reference-text", "Move footprint reference text to a footprint-local X/Y offset, not an absolute board coordinate.", S("reference"), N("xMm"), N("yMm"))
        ,Op("hide-reference-text", "Hide footprint reference text.", S("reference"))
        ,Op("cleanup-silkscreen", "Hide reference anchors that overlap within a threshold.", N("minimumSpacingMm"))
        ,Op("add-testpoint", "Add a through-hole testpoint on a net.", S("reference"), S("net"), N("xMm"), N("yMm"), N("diameterMm"))
        ,Op("set-board-pad-net", "Restore or change one board pad net by footprint reference and pad name.", S("reference"), S("pad"), S("net"))
        ,Op("add-mounting-hole", "Add an NPTH mounting hole.", S("reference"), N("xMm"), N("yMm"), N("drillMm"), N("diameterMm"))
        ,Op("add-mechanical-keepout", "Add a copper/mechanical keep-out polygon.", S("layer"), S("points"))
        ,Op("add-module-keepout", "Reserve a module underside from vias and copper pours while allowing its pads, masked tracks, and footprint.", S("layer"), S("points"))
        ,Op("set-board-outline-rectangle", "Set the exact coordinates of a single rectangular Edge.Cuts outline.", N("leftMm"), N("topMm"), N("rightMm"), N("bottomMm"))
    };

    public static IReadOnlyDictionary<string, DesignPlanOperationDefinition> ByType { get; } = All.ToDictionary(static item => item.Type, StringComparer.Ordinal);

    public static string? ValidateProperties(string type, JsonElement operation)
    {
        if (!ByType.TryGetValue(type, out var definition)) return null;
        var allowed = definition.Properties.Select(static p => p.Name).Append("id").Append("type").ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in operation.EnumerateObject())
            if (!allowed.Contains(candidate.Name)) return $"Operation {type} contains unsupported property: {candidate.Name}.";
        foreach (var property in definition.Properties)
        {
            if (!operation.TryGetProperty(property.Name, out var value))
            {
                if (property.Required) return $"Operation {type} requires property: {property.Name}.";
                continue;
            }
            var valid = property.Kind switch
            {
                DesignPlanPropertyKind.String => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()),
                DesignPlanPropertyKind.Number => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
                DesignPlanPropertyKind.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
                DesignPlanPropertyKind.Object => value.ValueKind == JsonValueKind.Object,
                _ => false
            };
            if (!valid) return $"Operation {type} property {property.Name} has an invalid value.";
        }
        return null;
    }

    public static string CreateJsonSchema()
    {
        object PropertySchema(DesignPlanPropertyDefinition property)
        {
            var type = property.Kind switch { DesignPlanPropertyKind.String => "string", DesignPlanPropertyKind.Integer => "integer", DesignPlanPropertyKind.Object => "object", _ => "number" };
            var values = new Dictionary<string, object?> { ["type"] = type };
            if (property.DefaultValue is not null) values["default"] = property.DefaultValue;
            return values;
        }
        var variants = All.Select(operation => new Dictionary<string, object?>
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["required"] = new[] { "id", "type" }.Concat(operation.Properties.Where(static p => p.Required).Select(static p => p.Name)).ToArray(),
            ["properties"] = new Dictionary<string, object?> { ["id"] = new { type = "string", minLength = 1 }, ["type"] = new { @const = operation.Type } }
                .Concat(operation.Properties.Select(property => new KeyValuePair<string, object?>(property.Name, PropertySchema(property))))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value)
        }).ToArray();
        object Requirement() => new { type = "string", @enum = new[] { "required", "optional", "skip" } };
        var schema = new Dictionary<string, object?>
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema", ["$id"] = AgentGuidanceService.DesignPlanSchemaUri,
            ["title"] = "PCBHelper Design Plan V1", ["type"] = "object", ["additionalProperties"] = false,
            ["required"] = new[] { "version", "goal", "operations" },
            ["properties"] = new Dictionary<string, object?>
            {
                ["version"] = new { @const = 1 }, ["goal"] = new { type = "string", minLength = 1 },
                ["operations"] = new { type = "array", minItems = 1, items = new Dictionary<string, object?> { ["oneOf"] = variants } },
                ["engineeringGate"] = new { type = "object", additionalProperties = false,
                    properties = new Dictionary<string, object?> { ["erc"] = Requirement(), ["drc"] = Requirement(), ["manufacturingValidation"] = Requirement(), ["simulationAssertions"] = Requirement(), ["designIntent"] = Requirement() } }
            }
        };
        return JsonSerializer.Serialize(schema, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    }

    private static DesignPlanOperationDefinition Op(string type, string description, params DesignPlanPropertyDefinition[] properties) => new(type, description, properties);
}
