using System.ComponentModel;
using ModelContextProtocol.Server;
using PCBHelper.Core;

namespace PCBHelper.Mcp;

[McpServerToolType]
public static class McpTools
{
    [McpServerTool(Name = "doctor"), Description("Check whether kicad-cli is installed and supported by PCBHelper.")]
    public static Task<ToolResponse<DoctorResult>> Doctor(CancellationToken cancellationToken)
    {
        return Services.Doctor.RunAsync(cancellationToken);
    }

    [McpServerTool(Name = "get_project_summary"), Description("Read the top-level KiCad project files in a project directory.")]
    public static ToolResponse<ProjectSummary> GetProjectSummary(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath)
    {
        return Services.ProjectDiscovery.GetSummary(projectPath);
    }

    [McpServerTool(Name = "get_board_summary"), Description("Read fixture-level footprint facts from a KiCad board file.")]
    public static ToolResponse<BoardSummary> GetBoardSummary(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath)
    {
        return Services.BoardSummary.GetSummary(projectPath);
    }

    [McpServerTool(Name = "measure_distance"), Description("Measure center-to-center distance between two board footprints.")]
    public static ToolResponse<MeasurementResult> MeasureDistance(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Reference of the starting footprint, for example R1.")] string fromReference,
        [Description("Reference of the ending footprint, for example D1.")] string toReference)
    {
        return Services.Geometry.MeasureDistance(projectPath, fromReference, toReference);
    }

    [McpServerTool(Name = "move_component_preview"), Description("Preview moving a footprint without writing the board file.")]
    public static Task<ToolResponse<ComponentMutationResult>> MoveComponentPreview(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Footprint reference, for example D1.")] string reference,
        [Description("Target X coordinate in millimeters.")] double xMillimeters,
        [Description("Target Y coordinate in millimeters.")] double yMillimeters,
        CancellationToken cancellationToken)
    {
        return Services.GeometryWorkflow.MoveComponentAsync(projectPath, reference, xMillimeters, yMillimeters, dryRun: true, cancellationToken);
    }

    [McpServerTool(Name = "move_component"), Description("Move a footprint by updating its top-level KiCad board position.")]
    public static Task<ToolResponse<ComponentMutationResult>> MoveComponent(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Footprint reference, for example D1.")] string reference,
        [Description("Target X coordinate in millimeters.")] double xMillimeters,
        [Description("Target Y coordinate in millimeters.")] double yMillimeters,
        CancellationToken cancellationToken)
    {
        return Services.GeometryWorkflow.MoveComponentAsync(projectPath, reference, xMillimeters, yMillimeters, dryRun: false, cancellationToken);
    }

    [McpServerTool(Name = "set_component_spacing_preview"), Description("Preview moving one footprint so it has target axis spacing from another footprint.")]
    public static Task<ToolResponse<ComponentSpacingMutationResult>> SetComponentSpacingPreview(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Footprint that stays fixed.")] string fixedReference,
        [Description("Footprint that will move.")] string movingReference,
        [Description("Target distance in millimeters.")] double distanceMillimeters,
        [Description("Axis to constrain spacing to: x or y. Defaults to x.")] string? axis = "x",
        CancellationToken cancellationToken = default)
    {
        return Services.GeometryWorkflow.SetComponentSpacingAsync(projectPath, fixedReference, movingReference, distanceMillimeters, axis, dryRun: true, cancellationToken);
    }

    [McpServerTool(Name = "set_component_spacing"), Description("Move one footprint so it has target axis spacing from another footprint.")]
    public static Task<ToolResponse<ComponentSpacingMutationResult>> SetComponentSpacing(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Footprint that stays fixed.")] string fixedReference,
        [Description("Footprint that will move.")] string movingReference,
        [Description("Target distance in millimeters.")] double distanceMillimeters,
        [Description("Axis to constrain spacing to: x or y. Defaults to x.")] string? axis = "x",
        CancellationToken cancellationToken = default)
    {
        return Services.GeometryWorkflow.SetComponentSpacingAsync(projectPath, fixedReference, movingReference, distanceMillimeters, axis, dryRun: false, cancellationToken);
    }

    [McpServerTool(Name = "restore_change_preview"), Description("Preview restoring a placement or value change report.")]
    public static Task<ToolResponse<object>> RestoreChangePreview(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Change id or path to a change.json report.")] string change,
        CancellationToken cancellationToken)
    {
        return Services.ChangeReview.RestoreChangeAsync(projectPath, change, dryRun: true, cancellationToken);
    }

    [McpServerTool(Name = "restore_change"), Description("Restore a placement or value change report.")]
    public static Task<ToolResponse<object>> RestoreChange(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Change id or path to a change.json report.")] string change,
        CancellationToken cancellationToken)
    {
        return Services.ChangeReview.RestoreChangeAsync(projectPath, change, dryRun: false, cancellationToken);
    }

    [McpServerTool(Name = "list_recent_changes"), Description("List PCBHelper change reports for a project.")]
    public static ToolResponse<ChangeListResult> ListRecentChanges(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath)
    {
        return Services.ChangeReview.ListChanges(projectPath);
    }

    [McpServerTool(Name = "get_change_report"), Description("Read a PCBHelper change report by id or path.")]
    public static ToolResponse<ChangeReport> GetChangeReport(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Change id or path to a change.json report.")] string change)
    {
        return Services.ChangeReview.GetChange(projectPath, change);
    }

    [McpServerTool(Name = "list_components"), Description("List component references and values from board and schematic files.")]
    public static ToolResponse<ComponentListResult> ListComponents(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath)
    {
        return Services.ComponentWorkflow.ListComponents(projectPath);
    }

    [McpServerTool(Name = "get_component_value"), Description("Read a component value from board and schematic files.")]
    public static ToolResponse<ComponentValueResult> GetComponentValue(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Component reference, for example R1.")] string reference)
    {
        return Services.ComponentWorkflow.GetValue(projectPath, reference);
    }

    [McpServerTool(Name = "set_component_value_preview"), Description("Preview changing a component value without writing files.")]
    public static Task<ToolResponse<ComponentValueMutationResult>> SetComponentValuePreview(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Component reference, for example R1.")] string reference,
        [Description("Exact value string to write, for example 300R.")] string value,
        [Description("Scope: available, schematic, board, or both. Defaults to available.")] string? scope = "available",
        CancellationToken cancellationToken = default)
    {
        return Services.ComponentWorkflow.SetValueAsync(projectPath, reference, value, scope, dryRun: true, cancellationToken);
    }

    [McpServerTool(Name = "set_component_value"), Description("Change a component value and write a PCBHelper change report.")]
    public static Task<ToolResponse<ComponentValueMutationResult>> SetComponentValue(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Component reference, for example R1.")] string reference,
        [Description("Exact value string to write, for example 300R.")] string value,
        [Description("Scope: available, schematic, board, or both. Defaults to available.")] string? scope = "available",
        CancellationToken cancellationToken = default)
    {
        return Services.ComponentWorkflow.SetValueAsync(projectPath, reference, value, scope, dryRun: false, cancellationToken);
    }

    [McpServerTool(Name = "list_nets"), Description("List board nets and their connected footprint pads.")]
    public static ToolResponse<NetListResult> ListNets(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath)
    {
        return Services.BoardInspection.ListNets(projectPath);
    }

    [McpServerTool(Name = "get_net_summary"), Description("Read one board net by name or numeric code.")]
    public static ToolResponse<NetSummary> GetNetSummary(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Net name or code.")] string net)
    {
        return Services.BoardInspection.GetNet(projectPath, net);
    }

    [McpServerTool(Name = "list_footprint_pads"), Description("List pads and net assignments for one footprint.")]
    public static ToolResponse<FootprintPadsResult> ListFootprintPads(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Footprint reference, for example R1.")] string reference)
    {
        return Services.BoardInspection.ListFootprintPads(projectPath, reference);
    }

    [McpServerTool(Name = "run_erc"), Description("Run KiCad ERC through kicad-cli for the project schematic.")]
    public static Task<ToolResponse<SingleCheckResult>> RunErc(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        CancellationToken cancellationToken)
    {
        return Services.CheckRunner.RunErcAsync(projectPath, cancellationToken);
    }

    [McpServerTool(Name = "run_drc"), Description("Run KiCad DRC through kicad-cli for the project board.")]
    public static Task<ToolResponse<SingleCheckResult>> RunDrc(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        CancellationToken cancellationToken)
    {
        return Services.CheckRunner.RunDrcAsync(projectPath, cancellationToken);
    }

    [McpServerTool(Name = "run_checks"), Description("Run all available KiCad checks through kicad-cli.")]
    public static Task<ToolResponse<CheckRunResult>> RunChecks(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        CancellationToken cancellationToken)
    {
        return Services.CheckRunner.RunChecksAsync(projectPath, cancellationToken);
    }

    [McpServerTool(Name = "get_check_summary"), Description("Run KiCad checks and return compact parsed findings plus raw report paths.")]
    public static Task<ToolResponse<CheckSummaryResult>> GetCheckSummary(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        CancellationToken cancellationToken)
    {
        return Services.CheckSummary.RunAsync(projectPath, cancellationToken);
    }

    [McpServerTool(Name = "export_gerbers"), Description("Export Gerber files through kicad-cli.")]
    public static Task<ToolResponse<SingleExportResult>> ExportGerbers(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        CancellationToken cancellationToken)
    {
        return Services.ExportService.ExportGerbersAsync(projectPath, cancellationToken);
    }

    [McpServerTool(Name = "export_drill"), Description("Export drill files through kicad-cli.")]
    public static Task<ToolResponse<SingleExportResult>> ExportDrill(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        CancellationToken cancellationToken)
    {
        return Services.ExportService.ExportDrillAsync(projectPath, cancellationToken);
    }

    [McpServerTool(Name = "export_manufacturing_files"), Description("Export generic manufacturing files through kicad-cli.")]
    public static Task<ToolResponse<ManufacturingExportResult>> ExportManufacturingFiles(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        CancellationToken cancellationToken)
    {
        return Services.ExportService.ExportManufacturingFilesAsync(projectPath, cancellationToken);
    }

    [McpServerTool(Name = "export_manufacturing_zip"), Description("Create a generic manufacturing zip from KiCad exports.")]
    public static Task<ToolResponse<ManufacturingPackageResult>> ExportManufacturingZip(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        CancellationToken cancellationToken)
    {
        return Services.PackageService.CreateManufacturingZipAsync(projectPath, cancellationToken);
    }

    [McpServerTool(Name = "export_bom"), Description("Export a BOM file through kicad-cli.")]
    public static Task<ToolResponse<SingleExportResult>> ExportBom(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        CancellationToken cancellationToken)
    {
        return Services.ExportService.ExportBomAsync(projectPath, cancellationToken);
    }

    [McpServerTool(Name = "export_position_files"), Description("Export footprint position files through kicad-cli.")]
    public static Task<ToolResponse<SingleExportResult>> ExportPositionFiles(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        CancellationToken cancellationToken)
    {
        return Services.ExportService.ExportPositionFilesAsync(projectPath, cancellationToken);
    }

    [McpServerTool(Name = "open_project_in_kicad"), Description("Open the KiCad project in the local KiCad GUI.")]
    public static ToolResponse<OpenProjectResult> OpenProjectInKiCad(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath)
    {
        return Services.OpenKiCad.OpenProject(projectPath, dryRun: false);
    }

    [McpServerTool(Name = "get_kicad_gui_capabilities"), Description("Detect whether KiCad GUI refresh/focus capabilities are available.")]
    public static Task<ToolResponse<KiCadGuiCapabilities>> GetKiCadGuiCapabilities(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        CancellationToken cancellationToken)
    {
        return Services.GuiReview.GetCapabilitiesAsync(projectPath, cancellationToken);
    }

    [McpServerTool(Name = "refresh_project_in_kicad"), Description("Refresh the KiCad project in the GUI if live IPC is available.")]
    public static Task<ToolResponse<KiCadGuiActionResult>> RefreshProjectInKiCad(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        CancellationToken cancellationToken)
    {
        return Services.GuiReview.RefreshProjectAsync(projectPath, cancellationToken);
    }

    [McpServerTool(Name = "focus_component_in_kicad"), Description("Focus a component in the KiCad GUI if live IPC is available.")]
    public static Task<ToolResponse<KiCadGuiActionResult>> FocusComponentInKiCad(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Footprint/component reference, for example R1.")] string reference,
        CancellationToken cancellationToken)
    {
        return Services.GuiReview.FocusComponentAsync(projectPath, reference, cancellationToken);
    }
}

internal static class Services
{
    private static readonly ProcessCommandRunner Runner = new();
    private static readonly KiCadCliLocator Locator = new();

    public static ProjectDiscoveryService ProjectDiscovery { get; } = new();

    public static BoardSummaryService BoardSummary { get; } = new(ProjectDiscovery);

    public static GeometryService Geometry { get; } = new(ProjectDiscovery);

    public static KiCadDoctorService Doctor { get; } = new(Locator, Runner);

    public static CheckRunner CheckRunner { get; } = new(ProjectDiscovery, Locator, Runner);

    public static ChangeReportService ChangeReports { get; } = new(ProjectDiscovery);

    public static GeometryWorkflowService GeometryWorkflow { get; } = new(Geometry, CheckRunner, ChangeReports);

    public static ComponentService ComponentService { get; } = new(ProjectDiscovery);

    public static ComponentValueWorkflowService ComponentWorkflow { get; } = new(ComponentService, CheckRunner, ChangeReports);

    public static ChangeReviewService ChangeReview { get; } = new(ProjectDiscovery, ChangeReports, GeometryWorkflow, ComponentWorkflow);

    public static BoardInspectionService BoardInspection { get; } = new(ProjectDiscovery);

    public static CheckSummaryService CheckSummary { get; } = new(CheckRunner);

    public static ExportService ExportService { get; } = new(ProjectDiscovery, Locator, Runner);

    public static PackageService PackageService { get; } = new(ProjectDiscovery, Doctor, ExportService);

    public static OpenKiCadService OpenKiCad { get; } = new(ProjectDiscovery, new KiCadExecutableLocator(Locator), new ProcessStarter());

    public static GuiReviewService GuiReview { get; } = new(Locator, new KiCadExecutableLocator(Locator), Runner);
}
