using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using PCBHelper.Core;

namespace PCBHelper.Mcp;

[McpServerToolType]
public sealed class WorkflowMcpTools
{
    private readonly PCBHelperRuntime _runtime;
    public WorkflowMcpTools(PCBHelperRuntime runtime) => _runtime = runtime;

    [McpServerTool(Name = "get_capabilities"), Description("Get the machine-readable PCBHelper agent contract, Design Plan operation catalog, and current limitations.")]
    public ToolResponse<ServerCapabilitiesResult> GetCapabilities() =>
        ToolResponse<ServerCapabilitiesResult>.Ok("PCBHelper workflow capabilities.",
            _runtime.AgentGuidance.GetCapabilities((Environment.GetEnvironmentVariable("PCBHELPER_MCP_PROFILE") ?? "workflow").Trim().ToLowerInvariant()));

    [McpServerTool(Name = "get_agent_guide"), Description("Get the canonical, versioned PCBHelper workflow and safety guidance for agents.")]
    public ToolResponse<AgentGuideResult> GetAgentGuide() =>
        ToolResponse<AgentGuideResult>.Ok("PCBHelper Agent Guide V1.", _runtime.AgentGuidance.GetGuide());

    [McpServerTool(Name = "create_project_from_template"), Description("Create a new, non-overwriting KiCad project from an approved blank two-layer template inside an authorized root.")]
    public ToolResponse<ProjectCreationResult> CreateProjectFromTemplate(
        string templateId, string projectName, string destinationDirectory,
        double? boardWidthMm = null, double? boardHeightMm = null, double? boardDiameterMm = null) =>
        _runtime.ProjectTemplates.CreateProjectFromTemplate(
            templateId, projectName, destinationDirectory, boardWidthMm, boardHeightMm, boardDiameterMm);

    [McpServerTool(Name = "get_project_context"), Description("Get concise project, board, schematic, component, net, KiCad capability, and transaction context without running ERC or DRC.")]
    public Task<ToolResponse<ProjectContextResult>> GetProjectContext(string projectPath, CancellationToken cancellationToken) =>
        _runtime.Workflows.GetProjectContextAsync(projectPath, cancellationToken);

    [McpServerTool(Name = "validate_design_plan"), Description("Validate a declarative PCBHelper Design Plan and return its canonical SHA-256 hash.")]
    public ToolResponse<DesignPlanValidationResult> ValidateDesignPlan(string projectPath, JsonElement plan) =>
        _runtime.Plans.Validate(projectPath, plan.GetRawText());

    [McpServerTool(Name = "preview_design_plan"), Description("Prepare a design plan in isolation without modifying the project.")]
    public ToolResponse<DesignPlanPreviewResult> PreviewDesignPlan(string projectPath, JsonElement plan) =>
        _runtime.Plans.Preview(projectPath, plan.GetRawText());

    [McpServerTool(Name = "apply_design_plan"), Description("Apply exactly the previewed plan as one transaction, then run its engineering gates.")]
    public Task<ToolResponse<DesignPlanApplyResult>> ApplyDesignPlan(
        string projectPath, JsonElement plan, string expectedPlanHash,
        [Description("Decision IDs returned by preview and explicitly acknowledged by the user.")] string[]? acknowledgedDecisionIds = null,
        CancellationToken cancellationToken = default) =>
        _runtime.Plans.ApplyAsync(projectPath, plan.GetRawText(), expectedPlanHash, acknowledgedDecisionIds, cancellationToken);

    [McpServerTool(Name = "get_transaction"), Description("Read one project transaction and its gate status.")]
    public ToolResponse<ProjectTransactionResult> GetTransaction(string projectPath, string transactionId) =>
        _runtime.Transactions.Get(projectPath, transactionId);

    [McpServerTool(Name = "restore_transaction"), Description("Restore a conflict-free design transaction. No force restore is available over MCP.")]
    public Task<ToolResponse<ProjectTransactionResult>> RestoreTransaction(string projectPath, string transactionId, CancellationToken cancellationToken) =>
        _runtime.Transactions.RestoreAsync(projectPath, transactionId, cancellationToken);

    [McpServerTool(Name = "run_engineering_gate"), Description("Run typed ERC, DRC, simulation, and manufacturing validation gates.")]
    public Task<ToolResponse<EngineeringGateResult>> RunEngineeringGate(
        string projectPath, string erc = "required", string drc = "required",
        string manufacturingValidation = "required", string simulationAssertions = "skip", string designIntent = "optional",
        CancellationToken cancellationToken = default) =>
        _runtime.Gates.RunAsync(projectPath, new EngineeringGateRequirements(erc, drc, manufacturingValidation, simulationAssertions, designIntent), cancellationToken);

    [McpServerTool(Name = "analyze_design_intent"), Description("Deterministically compare schematic, board test access, and sourced component evidence with the project's declared design intent.")]
    public ToolResponse<DesignIntentReport> AnalyzeDesignIntent(string projectPath) => _runtime.DesignIntent.Analyze(projectPath);

    [McpServerTool(Name = "get_design_intent_report"), Description("Read a previous project-scoped design-intent report by run id.")]
    public ToolResponse<DesignIntentReport> GetDesignIntentReport(string projectPath, string runId) => _runtime.DesignIntent.GetReport(projectPath, runId);

    [McpServerTool(Name = "generate_review_package"), Description("Generate an autonomous project review report with design and manufacturing context.")]
    public Task<ToolResponse<ReviewPackageResult>> GenerateReviewPackage(string projectPath, CancellationToken cancellationToken) =>
        _runtime.Workflows.GenerateReviewPackageAsync(projectPath, cancellationToken);

    [McpServerTool(Name = "generate_pcbway_package"), Description("Generate PCBWay manufacturing and assembly outputs after a gate-passed transaction. Does not place an order.")]
    public Task<ToolResponse<AssemblyPackageResult>> GeneratePcbWayPackage(string projectPath, CancellationToken cancellationToken) =>
        _runtime.Workflows.GeneratePcbWayPackageAsync(projectPath, cancellationToken);

    [McpServerTool(Name = "generate_pcbway_release"), Description("Run release gates and generate exactly a fabrication ZIP, BOM, CPL, order settings, and review report. Never places an order.")]
    public Task<ToolResponse<PcbWayReleaseResult>> GeneratePcbWayRelease(string projectPath, CancellationToken cancellationToken) =>
        _runtime.Releases.GenerateAsync(projectPath, cancellationToken);

    [McpServerTool(Name = "validate_release_requirements"), Description("Detect documented release requirements such as required testpoints or mounting holes and verify their board implementation.")]
    public ToolResponse<ReleaseRequirementsResult> ValidateReleaseRequirements(string projectPath) => _runtime.Releases.ValidateRequirements(projectPath);

    [McpServerTool(Name = "refill_zones"), Description("Request zone refill when supported; otherwise return a stable capability error without pretending success.")]
    public ToolResponse<BoardFinishingMutationResult> RefillZones(string projectPath) => _runtime.BoardFinishing.RefillZones(projectPath);

    [McpServerTool(Name = "get_simulation_capabilities"), Description("Report whether the deterministic ngspice simulation backend is available.")]
    public ToolResponse<SimulationCapabilities> GetSimulationCapabilities() =>
        ToolResponse<SimulationCapabilities>.Ok("Simulation capability status.", _runtime.Simulations.GetCapabilities());

    [McpServerTool(Name = "validate_simulation_tests"), Description("Validate constrained PCBHelper simulation test specifications without running a simulator.")]
    public ToolResponse<SimulationValidationResult> ValidateSimulationTests(string projectPath, string? testId = null) =>
        _runtime.Simulations.Validate(projectPath, testId);

    [McpServerTool(Name = "run_simulation_tests"), Description("Run deterministic simulation tests through ngspice and evaluate their assertions.")]
    public Task<ToolResponse<SimulationRunResult>> RunSimulationTests(string projectPath, string? testId = null, CancellationToken cancellationToken = default) =>
        _runtime.Simulations.RunAsync(projectPath, testId, cancellationToken);

    [McpServerTool(Name = "get_simulation_report"), Description("Read a previous project-scoped simulation report by run id.")]
    public ToolResponse<SimulationRunResult> GetSimulationReport(string projectPath, string runId) =>
        _runtime.Simulations.GetReport(projectPath, runId);

    [McpServerTool(Name = "validate_kicad_simulation_models"), Description("Validate KiCad schematic SPICE models and explicit semiconductor pin maps before netlist export.")]
    public ToolResponse<KiCadSimulationModelValidation> ValidateKiCadSimulationModels(string projectPath) => _runtime.KiCadSimulationNetlists.ValidateModels(projectPath);

    [McpServerTool(Name = "export_kicad_spice_netlist"), Description("Export a KiCad SPICE netlist only after model and pin-map validation passes.")]
    public Task<ToolResponse<KiCadSpiceNetlistResult>> ExportKiCadSpiceNetlist(string projectPath,CancellationToken cancellationToken) => _runtime.KiCadSimulationNetlists.ExportAsync(projectPath,cancellationToken);

    [McpServerTool(Name = "run_simulation_sweep"), Description("Run battery, tolerance, or noise scenarios using constrained circuit placeholders BATTERY_V, TOLERANCE_SCALE, or NOISE_V.")]
    public Task<ToolResponse<SimulationSweepResult>> RunSimulationSweep(string projectPath,string testId,string kind,double[] values,CancellationToken cancellationToken) => _runtime.Simulations.RunSweepAsync(projectPath,testId,kind,values,cancellationToken);
}
