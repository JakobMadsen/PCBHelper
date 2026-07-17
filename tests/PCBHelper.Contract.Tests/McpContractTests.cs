using System.Reflection;
using PCBHelper.Core;
using PCBHelper.Mcp;

namespace PCBHelper.Contract.Tests;

public sealed class McpContractTests
{
    [Fact]
    public void WorkflowTools_Expose_Exactly_The_Default_Workflow_Surface()
    {
        var toolNames = typeof(WorkflowMcpTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(static method => method.GetCustomAttributesData()
                .Any(static attribute => attribute.AttributeType.Name.Contains("McpServerTool", StringComparison.Ordinal)))
            .Select(GetToolName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(new HashSet<string>(StringComparer.Ordinal)
        {
            "get_capabilities", "get_agent_guide", "create_project_from_template",
            "get_project_context", "validate_design_plan", "preview_design_plan", "apply_design_plan",
            "preview_autoroute_board", "apply_autoroute_board",
            "preview_project_footprint_library", "apply_project_footprint_library",
            "get_transaction", "restore_transaction", "run_engineering_gate",
            "analyze_design_intent", "get_design_intent_report",
            "generate_review_package", "generate_pcbway_package", "generate_pcbway_release", "validate_release_requirements", "refill_zones", "get_simulation_capabilities",
            "validate_simulation_tests", "run_simulation_tests", "get_simulation_report", "validate_kicad_simulation_models", "export_kicad_spice_netlist", "run_simulation_sweep"
        }, toolNames);
    }

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
        Assert.Contains("list_unrouted_connections", toolNames);
        Assert.Contains("validate_track_clearance", toolNames);
        Assert.Contains("add_track_preview", toolNames);
        Assert.Contains("add_track", toolNames);
        Assert.Contains("add_track_polyline_preview", toolNames);
        Assert.Contains("add_track_polyline", toolNames);
        Assert.Contains("delete_track_preview", toolNames);
        Assert.Contains("delete_track", toolNames);
        Assert.Contains("add_via_preview", toolNames);
        Assert.Contains("add_via", toolNames);
        Assert.Contains("delete_via_preview", toolNames);
        Assert.Contains("delete_via", toolNames);
        Assert.Contains("setup_freerouting_preview", toolNames);
        Assert.Contains("setup_freerouting", toolNames);
        Assert.Contains("autoroute_board_preview", toolNames);
        Assert.Contains("autoroute_board", toolNames);
        Assert.Contains("list_schematic_symbols", toolNames);
        Assert.Contains("create_schematic_symbol_preview", toolNames);
        Assert.Contains("create_schematic_symbol", toolNames);
        Assert.Contains("set_symbol_field_preview", toolNames);
        Assert.Contains("set_symbol_field", toolNames);
        Assert.Contains("connect_schematic_pins_preview", toolNames);
        Assert.Contains("connect_schematic_pins", toolNames);
        Assert.Contains("add_net_label_preview", toolNames);
        Assert.Contains("add_net_label", toolNames);
        Assert.Contains("delete_net_label_by_uuid_preview", toolNames);
        Assert.Contains("delete_net_label_by_uuid", toolNames);
        Assert.Contains("delete_net_label_preview", toolNames);
        Assert.Contains("delete_net_label", toolNames);
        Assert.Contains("delete_schematic_wire_by_uuid_preview", toolNames);
        Assert.Contains("delete_schematic_wire_by_uuid", toolNames);
        Assert.Contains("delete_schematic_wire_preview", toolNames);
        Assert.Contains("delete_schematic_wire", toolNames);
        Assert.Contains("update_pcb_from_schematic_preview", toolNames);
        Assert.Contains("update_pcb_from_schematic", toolNames);
        Assert.Contains("regenerate_board_footprint_preview", toolNames);
        Assert.Contains("regenerate_board_footprint", toolNames);
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
        Assert.Contains("export_assembly_bom", toolNames);
        Assert.Contains("export_cpl", toolNames);
        Assert.Contains("validate_assembly_package", toolNames);
        Assert.Contains("export_pcbway_assembly_package", toolNames);
        Assert.Contains("list_test_specs", toolNames);
        Assert.Contains("validate_test_specs", toolNames);
        Assert.Contains("evaluate_test_results", toolNames);
        Assert.Contains("open_project_in_kicad", toolNames);
        Assert.Contains("get_kicad_gui_capabilities", toolNames);
        Assert.Contains("refresh_project_in_kicad", toolNames);
        Assert.Contains("focus_component_in_kicad", toolNames);
    }

    [Fact]
    public void CreateSchematicSymbol_Tools_Expose_Optional_Unit_Parameter()
    {
        foreach (var methodName in new[] { nameof(McpTools.CreateSchematicSymbolPreview), nameof(McpTools.CreateSchematicSymbol) })
        {
            var method = typeof(McpTools).GetMethod(methodName)!;
            var unit = method.GetParameters().Single(parameter => parameter.Name == "unit");

            Assert.True(unit.HasDefaultValue);
            Assert.Equal(1, unit.DefaultValue);
        }
    }

    [Fact]
    public void Client_Adapters_Reference_The_Canonical_Agent_Guide()
    {
        var paths = new[]
        {
            Path.Combine(RepoRoot.Path, "AGENTS.md"),
            Path.Combine(RepoRoot.Path, ".github", "copilot-instructions.md"),
            Path.Combine(RepoRoot.Path, ".agents", "skills", "pcbhelper-operator", "SKILL.md")
        };

        Assert.All(paths, path =>
        {
            var content = File.ReadAllText(path);
            Assert.Contains(AgentGuidanceService.GuideUri, content, StringComparison.Ordinal);
            Assert.Contains("get_capabilities", content, StringComparison.Ordinal);
        });
    }

    private static string GetToolName(MethodInfo method)
    {
        var attribute = method.GetCustomAttributesData()
            .First(static item => item.AttributeType.Name.Contains("McpServerTool", StringComparison.Ordinal));
        var named = attribute.NamedArguments.FirstOrDefault(static arg => arg.MemberName == "Name");

        return named.TypedValue.Value as string ?? method.Name;
    }
}
