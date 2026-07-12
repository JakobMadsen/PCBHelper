using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PCBHelper.Core;
using PCBHelper.Mcp;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

var profile = (Environment.GetEnvironmentVariable("PCBHELPER_MCP_PROFILE") ?? "workflow").Trim().ToLowerInvariant();
builder.Services.AddSingleton(PCBHelperRuntime.ForMcp());
var mcp = builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithResources<AgentMcpResources>()
    .WithPrompts<AgentMcpPrompts>();
switch (profile)
{
    case "workflow": mcp.WithTools<WorkflowMcpTools>(); break;
    case "legacy": mcp.WithTools(new[] { typeof(McpTools) }); break;
    case "all": mcp.WithTools<WorkflowMcpTools>().WithTools(new[] { typeof(McpTools) }); break;
    default: throw new InvalidOperationException("PCBHELPER_MCP_PROFILE must be workflow, legacy, or all.");
}

await builder.Build().RunAsync();
