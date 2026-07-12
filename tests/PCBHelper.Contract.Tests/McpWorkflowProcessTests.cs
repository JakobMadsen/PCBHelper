using System.Diagnostics;
using System.Text.Json;
using PCBHelper.Core;

namespace PCBHelper.Contract.Tests;

public sealed class McpWorkflowProcessTests
{
    [Fact]
    public async Task Default_Profile_Handshakes_And_Lists_Exactly_Workflow_Tools()
    {
        var project = Path.Combine(RepoRoot.Path, "src", "PCBHelper.Mcp", "PCBHelper.Mcp.csproj");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot.Path,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var configuration = Path.GetFileName(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!);
        start.ArgumentList.Add("run"); start.ArgumentList.Add("--no-build"); start.ArgumentList.Add("--configuration"); start.ArgumentList.Add(configuration); start.ArgumentList.Add("--project"); start.ArgumentList.Add(project);
        start.Environment["PCBHELPER_MCP_PROFILE"] = "workflow";
        start.Environment["PCBHELPER_ALLOWED_ROOTS"] = RepoRoot.Path;
        using var process = Process.Start(start)!;
        try
        {
            await Send(process, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "contract-test", version = "1" } } });
            var initialize = await ReadResponse(process, 1);
            Assert.Equal("2.0", initialize.GetProperty("jsonrpc").GetString());
            Assert.True(initialize.TryGetProperty("result", out var initializationResult), initialize.GetRawText());
            Assert.True(initializationResult.GetProperty("capabilities").TryGetProperty("tools", out _), initialize.GetRawText());
            await Send(process, new { jsonrpc = "2.0", method = "notifications/initialized" });
            await Send(process, new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } });
            var response = await ReadResponse(process, 2);
            Assert.True(response.TryGetProperty("result", out var result), response.GetRawText());
            var names = result.GetProperty("tools").EnumerateArray()
                .Select(static tool => tool.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(new HashSet<string>(StringComparer.Ordinal)
            {
                "get_capabilities", "get_agent_guide",
                "get_project_context", "validate_design_plan", "preview_design_plan", "apply_design_plan",
                "get_transaction", "restore_transaction", "run_engineering_gate",
                "analyze_design_intent", "get_design_intent_report",
                "generate_review_package", "generate_pcbway_package", "generate_pcbway_release", "validate_release_requirements", "refill_zones", "get_simulation_capabilities",
                "validate_simulation_tests", "run_simulation_tests", "get_simulation_report", "validate_kicad_simulation_models", "export_kicad_spice_netlist", "run_simulation_sweep"
            }, names);

            await Send(process, new { jsonrpc = "2.0", id = 3, method = "resources/list", @params = new { } });
            var resources = (await ReadResponse(process, 3)).GetProperty("result").GetProperty("resources").EnumerateArray().ToArray();
            Assert.Equal(new[] { AgentGuidanceService.GuideUri, AgentGuidanceService.DesignPlanSchemaUri },
                resources.Select(static resource => resource.GetProperty("uri").GetString()).Order().ToArray());

            await Send(process, new { jsonrpc = "2.0", id = 4, method = "resources/read", @params = new { uri = AgentGuidanceService.GuideUri } });
            var guide = await ReadResponse(process, 4);
            Assert.Contains("NO_RAW_KICAD_OR_SHELL", guide.GetRawText(), StringComparison.Ordinal);

            await Send(process, new { jsonrpc = "2.0", id = 5, method = "prompts/list", @params = new { } });
            var prompts = (await ReadResponse(process, 5)).GetProperty("result").GetProperty("prompts").EnumerateArray().ToArray();
            Assert.Contains(prompts, static prompt => prompt.GetProperty("name").GetString() == "operate_pcbhelper_project");

            await Send(process, new { jsonrpc = "2.0", id = 6, method = "prompts/get", @params = new { name = "operate_pcbhelper_project", arguments = new { projectPath = RepoRoot.Path, goal = "Inspect board" } } });
            var prompt = await ReadResponse(process, 6);
            Assert.Contains("get_capabilities", prompt.GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    private static async Task Send(Process process, object message)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await process.StandardInput.FlushAsync();
    }

    private static async Task<JsonElement> ReadResponse(Process process, int id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null) throw new InvalidOperationException(await process.StandardError.ReadToEndAsync(timeout.Token));
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("id", out var responseId) && responseId.TryGetInt32(out var value) && value == id)
                return document.RootElement.Clone();
        }
    }
}
