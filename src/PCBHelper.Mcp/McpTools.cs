using System.ComponentModel;
using ModelContextProtocol.Server;
using PCBHelper.Core;

namespace PCBHelper.Mcp;

[McpServerToolType]
public static class McpTools
{
    [McpServerTool(Name = "doctor"), Description("Check whether kicad-cli is installed and supported by PCBHelper.")]
    public static async Task<ToolResponse<DoctorResult>> Doctor(CancellationToken cancellationToken)
    {
        var result = await Services.Doctor.RunAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PCBHELPER_ALLOWED_ROOTS")))
        {
            return result with { Warnings = result.Warnings.Append("PCBHELPER_ALLOWED_ROOTS is not configured; MCP project reads and mutations are blocked.").ToArray() };
        }
        return result;
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

    [McpServerTool(Name = "list_tracks"), Description("List board track segments, optionally filtered by net.")]
    public static ToolResponse<TrackListResult> ListTracks(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Optional net name or code.")] string? net = null)
    {
        return Services.RoutingWorkflow.ListTracks(projectPath, net);
    }

    [McpServerTool(Name = "list_vias"), Description("List board vias, optionally filtered by net.")]
    public static ToolResponse<ViaListResult> ListVias(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Optional net name or code.")] string? net = null)
    {
        return Services.RoutingWorkflow.ListVias(projectPath, net);
    }

    [McpServerTool(Name = "get_net_routing"), Description("Read pads, tracks, and vias for one board net.")]
    public static ToolResponse<NetRoutingResult> GetNetRouting(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Net name or code.")] string net)
    {
        return Services.RoutingWorkflow.GetNetRouting(projectPath, net);
    }

    [McpServerTool(Name = "list_unrouted_connections"), Description("List disconnected pad groups per net so routing can target missing board connections.")]
    public static ToolResponse<UnroutedConnectionListResult> ListUnroutedConnections(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Optional net name or code.")] string? net = null)
    {
        return Services.RoutingWorkflow.ListUnroutedConnections(projectPath, net);
    }

    [McpServerTool(Name = "validate_track_clearance"), Description("Validate a proposed track polyline against conservative copper clearance before writing it.")]
    public static ToolResponse<RoutingClearanceValidationResult> ValidateTrackClearance(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Net name or code.")] string net,
        [Description("Polyline points as x1,y1;x2,y2;... in millimeters.")] string points,
        [Description("Copper layer: F.Cu or B.Cu.")] string layer,
        [Description("Track width in millimeters.")] double widthMillimeters)
    {
        return Services.RoutingWorkflow.ValidateTrackClearance(projectPath, net, points, layer, widthMillimeters);
    }

    [McpServerTool(Name = "add_track_preview"), Description("Preview adding a straight track segment without writing files.")]
    public static Task<ToolResponse<RoutingMutationResult>> AddTrackPreview(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Net name or code.")] string net,
        [Description("Start X coordinate in millimeters.")] double startXMillimeters,
        [Description("Start Y coordinate in millimeters.")] double startYMillimeters,
        [Description("End X coordinate in millimeters.")] double endXMillimeters,
        [Description("End Y coordinate in millimeters.")] double endYMillimeters,
        [Description("Copper layer: F.Cu or B.Cu.")] string layer,
        [Description("Track width in millimeters.")] double widthMillimeters,
        CancellationToken cancellationToken = default)
    {
        return Services.RoutingWorkflow.AddTrackAsync(projectPath, net, startXMillimeters, startYMillimeters, endXMillimeters, endYMillimeters, layer, widthMillimeters, dryRun: true, cancellationToken);
    }

    [McpServerTool(Name = "add_track"), Description("Add a straight track segment and write a PCBHelper change report.")]
    public static Task<ToolResponse<RoutingMutationResult>> AddTrack(
        string projectPath,
        string net,
        double startXMillimeters,
        double startYMillimeters,
        double endXMillimeters,
        double endYMillimeters,
        string layer,
        double widthMillimeters,
        CancellationToken cancellationToken = default)
    {
        return Services.RoutingWorkflow.AddTrackAsync(projectPath, net, startXMillimeters, startYMillimeters, endXMillimeters, endYMillimeters, layer, widthMillimeters, dryRun: false, cancellationToken);
    }

    [McpServerTool(Name = "add_track_polyline_preview"), Description("Preview adding an atomically validated track polyline without writing files.")]
    public static Task<ToolResponse<RoutingMutationResult>> AddTrackPolylinePreview(
        string projectPath,
        string net,
        string points,
        string layer,
        double widthMillimeters,
        CancellationToken cancellationToken = default)
    {
        return Services.RoutingWorkflow.AddTrackPolylineAsync(projectPath, net, points, layer, widthMillimeters, dryRun: true, cancellationToken);
    }

    [McpServerTool(Name = "add_track_polyline"), Description("Add an atomically validated track polyline and write a PCBHelper change report.")]
    public static Task<ToolResponse<RoutingMutationResult>> AddTrackPolyline(
        string projectPath,
        string net,
        string points,
        string layer,
        double widthMillimeters,
        CancellationToken cancellationToken = default)
    {
        return Services.RoutingWorkflow.AddTrackPolylineAsync(projectPath, net, points, layer, widthMillimeters, dryRun: false, cancellationToken);
    }

    [McpServerTool(Name = "delete_track_preview"), Description("Preview deleting a track segment without writing files.")]
    public static Task<ToolResponse<RoutingMutationResult>> DeleteTrackPreview(
        string projectPath,
        string track,
        CancellationToken cancellationToken = default)
    {
        return Services.RoutingWorkflow.DeleteTrackAsync(projectPath, track, dryRun: true, cancellationToken);
    }

    [McpServerTool(Name = "delete_track"), Description("Delete a track segment and write a PCBHelper change report.")]
    public static Task<ToolResponse<RoutingMutationResult>> DeleteTrack(
        string projectPath,
        string track,
        CancellationToken cancellationToken = default)
    {
        return Services.RoutingWorkflow.DeleteTrackAsync(projectPath, track, dryRun: false, cancellationToken);
    }

    [McpServerTool(Name = "add_via_preview"), Description("Preview adding a through via without writing files.")]
    public static Task<ToolResponse<RoutingMutationResult>> AddViaPreview(
        string projectPath,
        string net,
        double xMillimeters,
        double yMillimeters,
        double sizeMillimeters,
        double drillMillimeters,
        string layers,
        CancellationToken cancellationToken = default)
    {
        return Services.RoutingWorkflow.AddViaAsync(projectPath, net, xMillimeters, yMillimeters, sizeMillimeters, drillMillimeters, layers, dryRun: true, cancellationToken);
    }

    [McpServerTool(Name = "add_via"), Description("Add a through via and write a PCBHelper change report.")]
    public static Task<ToolResponse<RoutingMutationResult>> AddVia(
        string projectPath,
        string net,
        double xMillimeters,
        double yMillimeters,
        double sizeMillimeters,
        double drillMillimeters,
        string layers,
        CancellationToken cancellationToken = default)
    {
        return Services.RoutingWorkflow.AddViaAsync(projectPath, net, xMillimeters, yMillimeters, sizeMillimeters, drillMillimeters, layers, dryRun: false, cancellationToken);
    }

    [McpServerTool(Name = "delete_via_preview"), Description("Preview deleting a via without writing files.")]
    public static Task<ToolResponse<RoutingMutationResult>> DeleteViaPreview(
        string projectPath,
        string via,
        CancellationToken cancellationToken = default)
    {
        return Services.RoutingWorkflow.DeleteViaAsync(projectPath, via, dryRun: true, cancellationToken);
    }

    [McpServerTool(Name = "delete_via"), Description("Delete a via and write a PCBHelper change report.")]
    public static Task<ToolResponse<RoutingMutationResult>> DeleteVia(
        string projectPath,
        string via,
        CancellationToken cancellationToken = default)
    {
        return Services.RoutingWorkflow.DeleteViaAsync(projectPath, via, dryRun: false, cancellationToken);
    }

    [McpServerTool(Name = "setup_freerouting_preview"), Description("Preview downloading FreeRouting into PCBHelper's local tools cache.")]
    public static Task<ToolResponse<FreeRoutingSetupResult>> SetupFreeRoutingPreview(CancellationToken cancellationToken = default)
    {
        return Services.FreeRoutingSetup.SetupAsync(dryRun: true, cancellationToken);
    }

    [McpServerTool(Name = "setup_freerouting"), Description("Download FreeRouting into PCBHelper's local tools cache so autoroute_board can discover it.")]
    public static Task<ToolResponse<FreeRoutingSetupResult>> SetupFreeRouting(CancellationToken cancellationToken = default)
    {
        return Services.FreeRoutingSetup.SetupAsync(dryRun: false, cancellationToken);
    }

    [McpServerTool(Name = "autoroute_board_preview"), Description("Preview whether the FreeRouting DSN/SES backend is available for this board.")]
    public static Task<ToolResponse<AutorouteBoardResult>> AutorouteBoardPreview(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        return Services.Autorouting.AutorouteBoardAsync(projectPath, dryRun: true, cancellationToken);
    }

    [McpServerTool(Name = "autoroute_board"), Description("Run the FreeRouting DSN/SES autorouting backend when local prerequisites are available.")]
    public static Task<ToolResponse<AutorouteBoardResult>> AutorouteBoard(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        return Services.Autorouting.AutorouteBoardAsync(projectPath, dryRun: false, cancellationToken);
    }

    [McpServerTool(Name = "list_schematic_symbols"), Description("List schematic symbols, fields, wires, and labels.")]
    public static ToolResponse<SchematicSymbolListResult> ListSchematicSymbols(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath)
    {
        return Services.SchematicWorkflow.ListSymbols(projectPath);
    }

    [McpServerTool(Name = "create_schematic_symbol_preview"), Description("Preview placing an approved catalog schematic symbol.")]
    public static Task<ToolResponse<SchematicMutationResult>> CreateSchematicSymbolPreview(
        string projectPath,
        string symbol,
        string reference,
        double xMillimeters,
        double yMillimeters,
        int unit = 1,
        string? value = null,
        string? footprint = null,
        CancellationToken cancellationToken = default)
    {
        return Services.SchematicWorkflow.CreateSymbolAsync(projectPath, symbol, reference, xMillimeters, yMillimeters, value, footprint, unit, dryRun: true, cancellationToken);
    }

    [McpServerTool(Name = "create_schematic_symbol"), Description("Place an approved catalog schematic symbol and write a change report.")]
    public static Task<ToolResponse<SchematicMutationResult>> CreateSchematicSymbol(
        string projectPath,
        string symbol,
        string reference,
        double xMillimeters,
        double yMillimeters,
        int unit = 1,
        string? value = null,
        string? footprint = null,
        CancellationToken cancellationToken = default)
    {
        return Services.SchematicWorkflow.CreateSymbolAsync(projectPath, symbol, reference, xMillimeters, yMillimeters, value, footprint, unit, dryRun: false, cancellationToken);
    }

    [McpServerTool(Name = "set_symbol_field_preview"), Description("Preview setting a schematic symbol field.")]
    public static Task<ToolResponse<SchematicMutationResult>> SetSymbolFieldPreview(
        string projectPath,
        string reference,
        string field,
        string value,
        CancellationToken cancellationToken = default)
    {
        return Services.SchematicWorkflow.SetSymbolFieldAsync(projectPath, reference, field, value, dryRun: true, cancellationToken);
    }

    [McpServerTool(Name = "set_symbol_field"), Description("Set a schematic symbol field and write a change report.")]
    public static Task<ToolResponse<SchematicMutationResult>> SetSymbolField(
        string projectPath,
        string reference,
        string field,
        string value,
        CancellationToken cancellationToken = default)
    {
        return Services.SchematicWorkflow.SetSymbolFieldAsync(projectPath, reference, field, value, dryRun: false, cancellationToken);
    }

    [McpServerTool(Name = "connect_schematic_pins_preview"), Description("Preview wiring two approved catalog schematic pins.")]
    public static Task<ToolResponse<SchematicMutationResult>> ConnectSchematicPinsPreview(
        string projectPath,
        string from,
        string to,
        string? net = null,
        CancellationToken cancellationToken = default)
    {
        return Services.SchematicWorkflow.ConnectPinsAsync(projectPath, from, to, net, dryRun: true, cancellationToken);
    }

    [McpServerTool(Name = "connect_schematic_pins"), Description("Wire two approved catalog schematic pins and write a change report.")]
    public static Task<ToolResponse<SchematicMutationResult>> ConnectSchematicPins(
        string projectPath,
        string from,
        string to,
        string? net = null,
        CancellationToken cancellationToken = default)
    {
        return Services.SchematicWorkflow.ConnectPinsAsync(projectPath, from, to, net, dryRun: false, cancellationToken);
    }

    [McpServerTool(Name = "add_net_label_preview"), Description("Preview adding a schematic net label.")]
    public static Task<ToolResponse<SchematicMutationResult>> AddNetLabelPreview(
        string projectPath,
        string net,
        double xMillimeters,
        double yMillimeters,
        CancellationToken cancellationToken = default)
    {
        return Services.SchematicWorkflow.AddNetLabelAsync(projectPath, net, xMillimeters, yMillimeters, dryRun: true, cancellationToken);
    }

    [McpServerTool(Name = "add_net_label"), Description("Add a schematic net label and write a change report.")]
    public static Task<ToolResponse<SchematicMutationResult>> AddNetLabel(
        string projectPath,
        string net,
        double xMillimeters,
        double yMillimeters,
        CancellationToken cancellationToken = default)
    {
        return Services.SchematicWorkflow.AddNetLabelAsync(projectPath, net, xMillimeters, yMillimeters, dryRun: false, cancellationToken);
    }

    [McpServerTool(Name = "delete_net_label_by_uuid_preview"), Description("Preview deleting a schematic net label by UUID.")]
    public static Task<ToolResponse<SchematicMutationResult>> DeleteNetLabelByUuidPreview(
        string projectPath,
        string uuid,
        CancellationToken cancellationToken = default)
    {
        return Services.SchematicWorkflow.DeleteNetLabelByUuidAsync(projectPath, uuid, dryRun: true, cancellationToken);
    }

    [McpServerTool(Name = "delete_net_label_by_uuid"), Description("Delete a schematic net label by UUID and write a change report.")]
    public static Task<ToolResponse<SchematicMutationResult>> DeleteNetLabelByUuid(
        string projectPath,
        string uuid,
        CancellationToken cancellationToken = default)
    {
        return Services.SchematicWorkflow.DeleteNetLabelByUuidAsync(projectPath, uuid, dryRun: false, cancellationToken);
    }

    [McpServerTool(Name = "delete_net_label_preview"), Description("Preview deleting a schematic net label by net name and coordinate.")]
    public static Task<ToolResponse<SchematicMutationResult>> DeleteNetLabelPreview(
        string projectPath,
        string net,
        double xMillimeters,
        double yMillimeters,
        double? toleranceMillimeters = null,
        CancellationToken cancellationToken = default)
    {
        return Services.SchematicWorkflow.DeleteNetLabelAsync(projectPath, net, xMillimeters, yMillimeters, toleranceMillimeters, dryRun: true, cancellationToken);
    }

    [McpServerTool(Name = "delete_net_label"), Description("Delete a schematic net label by net name and coordinate and write a change report.")]
    public static Task<ToolResponse<SchematicMutationResult>> DeleteNetLabel(
        string projectPath,
        string net,
        double xMillimeters,
        double yMillimeters,
        double? toleranceMillimeters = null,
        CancellationToken cancellationToken = default)
    {
        return Services.SchematicWorkflow.DeleteNetLabelAsync(projectPath, net, xMillimeters, yMillimeters, toleranceMillimeters, dryRun: false, cancellationToken);
    }

    [McpServerTool(Name = "delete_schematic_wire_by_uuid_preview"), Description("Preview deleting a schematic wire by UUID.")]
    public static Task<ToolResponse<SchematicMutationResult>> DeleteSchematicWireByUuidPreview(
        string projectPath,
        string uuid,
        CancellationToken cancellationToken = default)
    {
        return Services.SchematicWorkflow.DeleteSchematicWireByUuidAsync(projectPath, uuid, dryRun: true, cancellationToken);
    }

    [McpServerTool(Name = "delete_schematic_wire_by_uuid"), Description("Delete a schematic wire by UUID and write a change report.")]
    public static Task<ToolResponse<SchematicMutationResult>> DeleteSchematicWireByUuid(
        string projectPath,
        string uuid,
        CancellationToken cancellationToken = default)
    {
        return Services.SchematicWorkflow.DeleteSchematicWireByUuidAsync(projectPath, uuid, dryRun: false, cancellationToken);
    }

    [McpServerTool(Name = "delete_schematic_wire_preview"), Description("Preview deleting a schematic wire by endpoints.")]
    public static Task<ToolResponse<SchematicMutationResult>> DeleteSchematicWirePreview(
        string projectPath,
        double x1Millimeters,
        double y1Millimeters,
        double x2Millimeters,
        double y2Millimeters,
        double? toleranceMillimeters = null,
        CancellationToken cancellationToken = default)
    {
        return Services.SchematicWorkflow.DeleteSchematicWireAsync(projectPath, x1Millimeters, y1Millimeters, x2Millimeters, y2Millimeters, toleranceMillimeters, dryRun: true, cancellationToken);
    }

    [McpServerTool(Name = "delete_schematic_wire"), Description("Delete a schematic wire by endpoints and write a change report.")]
    public static Task<ToolResponse<SchematicMutationResult>> DeleteSchematicWire(
        string projectPath,
        double x1Millimeters,
        double y1Millimeters,
        double x2Millimeters,
        double y2Millimeters,
        double? toleranceMillimeters = null,
        CancellationToken cancellationToken = default)
    {
        return Services.SchematicWorkflow.DeleteSchematicWireAsync(projectPath, x1Millimeters, y1Millimeters, x2Millimeters, y2Millimeters, toleranceMillimeters, dryRun: false, cancellationToken);
    }

    [McpServerTool(Name = "update_pcb_from_schematic_preview"), Description("Preview creating missing board footprints from approved schematic symbols.")]
    public static Task<ToolResponse<SchematicMutationResult>> UpdatePcbFromSchematicPreview(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        return Services.SchematicWorkflow.UpdatePcbFromSchematicAsync(projectPath, dryRun: true, cancellationToken);
    }

    [McpServerTool(Name = "update_pcb_from_schematic"), Description("Create missing board footprints from approved schematic symbols and write a change report.")]
    public static Task<ToolResponse<SchematicMutationResult>> UpdatePcbFromSchematic(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        return Services.SchematicWorkflow.UpdatePcbFromSchematicAsync(projectPath, dryRun: false, cancellationToken);
    }

    [McpServerTool(Name = "regenerate_board_footprint_preview"), Description("Preview regenerating one existing board footprint from its approved schematic template.")]
    public static Task<ToolResponse<SchematicMutationResult>> RegenerateBoardFootprintPreview(
        string projectPath,
        string reference,
        CancellationToken cancellationToken = default)
    {
        return Services.SchematicWorkflow.RegenerateBoardFootprintAsync(projectPath, reference, dryRun: true, cancellationToken);
    }

    [McpServerTool(Name = "regenerate_board_footprint"), Description("Regenerate one existing board footprint from its approved schematic template and write a change report.")]
    public static Task<ToolResponse<SchematicMutationResult>> RegenerateBoardFootprint(
        string projectPath,
        string reference,
        CancellationToken cancellationToken = default)
    {
        return Services.SchematicWorkflow.RegenerateBoardFootprintAsync(projectPath, reference, dryRun: false, cancellationToken);
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

    [McpServerTool(Name = "export_assembly_bom"), Description("Export a PCBWay-oriented assembly BOM CSV.")]
    public static Task<ToolResponse<AssemblyExportResult>> ExportAssemblyBom(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        CancellationToken cancellationToken)
    {
        return Services.AssemblyService.ExportAssemblyBomAsync(projectPath, cancellationToken);
    }

    [McpServerTool(Name = "export_cpl"), Description("Export a PCBWay-oriented component placement/centroid CSV.")]
    public static Task<ToolResponse<AssemblyExportResult>> ExportCpl(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        CancellationToken cancellationToken)
    {
        return Services.AssemblyService.ExportCplAsync(projectPath, cancellationToken);
    }

    [McpServerTool(Name = "validate_assembly_package"), Description("Validate assembly BOM/CPL readiness for PCBWay.")]
    public static ToolResponse<AssemblyValidationResult> ValidateAssemblyPackage(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath)
    {
        return Services.AssemblyService.ValidateAssemblyPackage(projectPath);
    }

    [McpServerTool(Name = "export_pcbway_assembly_package"), Description("Create a PCBWay assembly zip with Gerber, drill, BOM, CPL, and validation report.")]
    public static Task<ToolResponse<AssemblyPackageResult>> ExportPcbWayAssemblyPackage(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        CancellationToken cancellationToken)
    {
        return Services.AssemblyService.CreatePcbWayAssemblyPackageAsync(projectPath, cancellationToken);
    }

    [McpServerTool(Name = "list_test_specs"), Description("List PCBHelper assertion test specs from .pcbhelper/tests/*.json.")]
    public static ToolResponse<TestSpecListResult> ListTestSpecs(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath)
    {
        return Services.TestSpecService.ListTests(projectPath);
    }

    [McpServerTool(Name = "validate_test_specs"), Description("Validate PCBHelper assertion test specs without running a simulator.")]
    public static ToolResponse<TestSpecValidationResult> ValidateTestSpecs(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath)
    {
        return Services.TestSpecService.ValidateTests(projectPath);
    }

    [McpServerTool(Name = "evaluate_test_results"), Description("Evaluate external measurement results against PCBHelper assertion test specs.")]
    public static ToolResponse<TestEvaluationResult> EvaluateTestResults(
        [Description("Path to a KiCad project directory or .kicad_pro file.")] string projectPath,
        [Description("Path to a JSON measurement result file.")] string resultsPath)
    {
        return Services.TestSpecService.EvaluateResults(projectPath, resultsPath);
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

    public static ProjectDiscoveryService ProjectDiscovery { get; } = new(ProjectScopePolicy.FromEnvironment());

    public static BoardSummaryService BoardSummary { get; } = new(ProjectDiscovery);

    public static GeometryService Geometry { get; } = new(ProjectDiscovery);

    public static KiCadDoctorService Doctor { get; } = new(Locator, Runner);

    public static CheckRunner CheckRunner { get; } = new(ProjectDiscovery, Locator, Runner);

    public static ChangeReportService ChangeReports { get; } = new(ProjectDiscovery);

    public static GeometryWorkflowService GeometryWorkflow { get; } = new(Geometry, CheckRunner, ChangeReports);

    public static ComponentService ComponentService { get; } = new(ProjectDiscovery);

    public static ComponentValueWorkflowService ComponentWorkflow { get; } = new(ComponentService, CheckRunner, ChangeReports);

    public static BoardInspectionService BoardInspection { get; } = new(ProjectDiscovery);

    public static RoutingService RoutingService { get; } = new(ProjectDiscovery);

    public static RoutingWorkflowService RoutingWorkflow { get; } = new(RoutingService, CheckRunner, ChangeReports);

    public static AutoroutingService Autorouting { get; } = new(ProjectDiscovery, Locator, Runner);

    public static FreeRoutingSetupService FreeRoutingSetup { get; } = new();

    public static SchematicAuthoringService SchematicService { get; } = new(ProjectDiscovery);

    public static SchematicAuthoringWorkflowService SchematicWorkflow { get; } = new(SchematicService, CheckRunner, ChangeReports);

    public static ChangeReviewService ChangeReview { get; } = new(ProjectDiscovery, ChangeReports, GeometryWorkflow, ComponentWorkflow, RoutingWorkflow, SchematicWorkflow);

    public static TestSpecService TestSpecService { get; } = new(ProjectDiscovery);

    public static CheckSummaryService CheckSummary { get; } = new(CheckRunner);

    public static ExportService ExportService { get; } = new(ProjectDiscovery, Locator, Runner);

    public static PackageService PackageService { get; } = new(ProjectDiscovery, Doctor, ExportService);

    public static AssemblyService AssemblyService { get; } = new(ProjectDiscovery, Doctor, ExportService);

    public static OpenKiCadService OpenKiCad { get; } = new(ProjectDiscovery, new KiCadExecutableLocator(Locator), new ProcessStarter());

    public static GuiReviewService GuiReview { get; } = new(Locator, new KiCadExecutableLocator(Locator), Runner);
}
