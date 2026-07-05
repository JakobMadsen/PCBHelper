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
        Assert.Contains("run_erc", toolNames);
        Assert.Contains("run_drc", toolNames);
        Assert.Contains("run_checks", toolNames);
        Assert.Contains("export_gerbers", toolNames);
        Assert.Contains("export_drill", toolNames);
        Assert.Contains("export_manufacturing_files", toolNames);
        Assert.Contains("export_manufacturing_zip", toolNames);
    }

    private static string GetToolName(MethodInfo method)
    {
        var attribute = method.GetCustomAttributesData()
            .First(static item => item.AttributeType.Name.Contains("McpServerTool", StringComparison.Ordinal));
        var named = attribute.NamedArguments.FirstOrDefault(static arg => arg.MemberName == "Name");

        return named.TypedValue.Value as string ?? method.Name;
    }
}
