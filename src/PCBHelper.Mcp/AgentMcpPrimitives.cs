using System.ComponentModel;
using ModelContextProtocol.Server;
using PCBHelper.Core;

namespace PCBHelper.Mcp;

[McpServerResourceType]
public sealed class AgentMcpResources
{
    private readonly PCBHelperRuntime _runtime;
    public AgentMcpResources(PCBHelperRuntime runtime) => _runtime = runtime;

    [McpServerResource(Name = "pcbhelper-agent-guide-v1", UriTemplate = AgentGuidanceService.GuideUri, MimeType = "text/markdown"),
     Description("Canonical PCBHelper Agent Guide V1.")]
    public string GetAgentGuide() => _runtime.AgentGuidance.GetGuide().Markdown;

    [McpServerResource(Name = "pcbhelper-design-plan-v1-schema", UriTemplate = AgentGuidanceService.DesignPlanSchemaUri, MimeType = "application/schema+json"),
     Description("Machine-readable JSON Schema for PCBHelper Design Plan V1.")]
    public string GetDesignPlanSchema() => _runtime.AgentGuidance.GetDesignPlanSchema();
}

[McpServerPromptType]
public sealed class AgentMcpPrompts
{
    [McpServerPrompt(Name = "operate_pcbhelper_project"), Description("Start a safe, autonomous PCBHelper workflow for one project goal.")]
    public string OperatePcbHelperProject(
        [Description("Authorized PCBHelper project root.")] string projectPath,
        [Description("The user's intended PCB outcome.")] string goal) => $$"""
        Operate PCBHelper project {{projectPath}} to achieve: {{goal}}

        First read pcbhelper://agent-guide/v1 or call get_agent_guide, then call get_capabilities and get_project_context.
        Use one coherent Design Plan, validate and preview it, and apply only the identical returned planHash.
        Run the required engineering gates and autonomously correct ordinary findings. Do not use raw KiCad edits,
        shell commands, or GUI automation as a mutation fallback. Do not order, pay, publish, or approve substitutions.
        """;
}
