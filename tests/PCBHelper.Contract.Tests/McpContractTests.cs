using System.Reflection;
using PCBHelper.Mcp;

namespace PCBHelper.Contract.Tests;

public sealed class McpContractTests
{
    [Fact]
    public void McpTools_Expose_First_Slice_Tool_Names()
    {
        var toolNames = typeof(McpTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(static method => method.GetCustomAttributesData()
                .Any(static attribute => attribute.AttributeType.Name.Contains("McpServerTool", StringComparison.Ordinal)))
            .Select(GetToolName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("doctor", toolNames);
        Assert.Contains("get_project_summary", toolNames);
        Assert.Contains("get_board_summary", toolNames);
        Assert.Contains("measure_distance", toolNames);
        Assert.Contains("move_component_preview", toolNames);
        Assert.Contains("move_component", toolNames);
        Assert.Contains("set_component_spacing_preview", toolNames);
        Assert.Contains("set_component_spacing", toolNames);
        Assert.Contains("restore_change_preview", toolNames);
        Assert.Contains("restore_change", toolNames);
        Assert.Contains("list_recent_changes", toolNames);
        Assert.Contains("get_change_report", toolNames);
        Assert.Contains("list_components", toolNames);
        Assert.Contains("get_component_value", toolNames);
        Assert.Contains("set_component_value_preview", toolNames);
        Assert.Contains("set_component_value", toolNames);
        Assert.Contains("list_nets", toolNames);
        Assert.Contains("get_net_summary", toolNames);
        Assert.Contains("list_footprint_pads", toolNames);
        Assert.Contains("list_tracks", toolNames);
        Assert.Contains("list_vias", toolNames);
        Assert.Contains("get_net_routing", toolNames);
        Assert.Contains("add_track_preview", toolNames);
        Assert.Contains("add_track", toolNames);
        Assert.Contains("delete_track_preview", toolNames);
        Assert.Contains("delete_track", toolNames);
        Assert.Contains("add_via_preview", toolNames);
        Assert.Contains("add_via", toolNames);
        Assert.Contains("delete_via_preview", toolNames);
        Assert.Contains("delete_via", toolNames);
        Assert.Contains("list_schematic_symbols", toolNames);
        Assert.Contains("create_schematic_symbol_preview", toolNames);
        Assert.Contains("create_schematic_symbol", toolNames);
        Assert.Contains("set_symbol_field_preview", toolNames);
        Assert.Contains("set_symbol_field", toolNames);
        Assert.Contains("connect_schematic_pins_preview", toolNames);
        Assert.Contains("connect_schematic_pins", toolNames);
        Assert.Contains("add_net_label_preview", toolNames);
        Assert.Contains("add_net_label", toolNames);
        Assert.Contains("update_pcb_from_schematic_preview", toolNames);
        Assert.Contains("update_pcb_from_schematic", toolNames);
        Assert.Contains("run_erc", toolNames);
        Assert.Contains("run_drc", toolNames);
        Assert.Contains("run_checks", toolNames);
        Assert.Contains("get_check_summary", toolNames);
        Assert.Contains("export_gerbers", toolNames);
        Assert.Contains("export_drill", toolNames);
        Assert.Contains("export_manufacturing_files", toolNames);
        Assert.Contains("export_manufacturing_zip", toolNames);
        Assert.Contains("export_bom", toolNames);
        Assert.Contains("export_position_files", toolNames);
        Assert.Contains("open_project_in_kicad", toolNames);
        Assert.Contains("get_kicad_gui_capabilities", toolNames);
        Assert.Contains("refresh_project_in_kicad", toolNames);
        Assert.Contains("focus_component_in_kicad", toolNames);
    }

    private static string GetToolName(MethodInfo method)
    {
        var attribute = method.GetCustomAttributesData()
            .First(static item => item.AttributeType.Name.Contains("McpServerTool", StringComparison.Ordinal));
        var named = attribute.NamedArguments.FirstOrDefault(static arg => arg.MemberName == "Name");

        return named.TypedValue.Value as string ?? method.Name;
    }
}
