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
    public static ToolResponse<ComponentMoveResult> MoveComponentPreview(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Footprint reference, for example D1.")] string reference,
        [Description("Target X coordinate in millimeters.")] double xMillimeters,
        [Description("Target Y coordinate in millimeters.")] double yMillimeters)
    {
        return Services.Geometry.MoveComponent(projectPath, reference, xMillimeters, yMillimeters, dryRun: true);
    }

    [McpServerTool(Name = "move_component"), Description("Move a footprint by updating its top-level KiCad board position.")]
    public static ToolResponse<ComponentMoveResult> MoveComponent(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Footprint reference, for example D1.")] string reference,
        [Description("Target X coordinate in millimeters.")] double xMillimeters,
        [Description("Target Y coordinate in millimeters.")] double yMillimeters)
    {
        return Services.Geometry.MoveComponent(projectPath, reference, xMillimeters, yMillimeters, dryRun: false);
    }

    [McpServerTool(Name = "set_component_spacing_preview"), Description("Preview moving one footprint so it has target axis spacing from another footprint.")]
    public static ToolResponse<ComponentSpacingResult> SetComponentSpacingPreview(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Footprint that stays fixed.")] string fixedReference,
        [Description("Footprint that will move.")] string movingReference,
        [Description("Target distance in millimeters.")] double distanceMillimeters,
        [Description("Axis to constrain spacing to: x or y. Defaults to x.")] string? axis = "x")
    {
        return Services.Geometry.SetComponentSpacing(projectPath, fixedReference, movingReference, distanceMillimeters, axis, dryRun: true);
    }

    [McpServerTool(Name = "set_component_spacing"), Description("Move one footprint so it has target axis spacing from another footprint.")]
    public static ToolResponse<ComponentSpacingResult> SetComponentSpacing(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Footprint that stays fixed.")] string fixedReference,
        [Description("Footprint that will move.")] string movingReference,
        [Description("Target distance in millimeters.")] double distanceMillimeters,
        [Description("Axis to constrain spacing to: x or y. Defaults to x.")] string? axis = "x")
    {
        return Services.Geometry.SetComponentSpacing(projectPath, fixedReference, movingReference, distanceMillimeters, axis, dryRun: false);
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

    public static ExportService ExportService { get; } = new(ProjectDiscovery, Locator, Runner);

    public static PackageService PackageService { get; } = new(ProjectDiscovery, Doctor, ExportService);
}
