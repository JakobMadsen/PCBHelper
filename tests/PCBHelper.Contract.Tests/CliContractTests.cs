using System.Diagnostics;
using System.Text.Json;

namespace PCBHelper.Contract.Tests;

public sealed class CliContractTests
{
    [Fact]
    public async Task Doctor_Json_Returns_Stable_Envelope()
    {
        var result = await RunCliAsync("doctor", "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("success", out _));
        Assert.True(root.TryGetProperty("summary", out _));
        Assert.True(root.TryGetProperty("data", out _));
        Assert.True(root.TryGetProperty("warnings", out _));
        Assert.True(root.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Summary_Json_Returns_Project_Shape()
    {
        var fixture = Path.Combine(RepoRoot.Path, "fixtures", "minimal-board");
        var result = await RunCliAsync("summary", fixture, "--json");

        Assert.Equal(0, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var data = document.RootElement.GetProperty("data");

        Assert.Equal("minimal-board", data.GetProperty("projectName").GetString());
        Assert.True(data.TryGetProperty("projectRoot", out _));
        Assert.True(data.TryGetProperty("projectFile", out _));
        Assert.True(data.TryGetProperty("schematicFile", out _));
        Assert.True(data.TryGetProperty("boardFile", out _));
        Assert.True(data.TryGetProperty("missingFiles", out _));
    }

    [Fact]
    public async Task Check_Json_Returns_Stable_Envelope_When_KiCad_Is_Missing()
    {
        var fixture = Path.Combine(RepoRoot.Path, "fixtures", "minimal-board");
        var result = await RunCliAsync("check", fixture, "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("success", out _));
        Assert.True(root.TryGetProperty("summary", out _));
        Assert.True(root.TryGetProperty("data", out _));
        Assert.True(root.TryGetProperty("warnings", out _));
        Assert.True(root.TryGetProperty("error", out _));
    }

    private static async Task<ProcessResult> RunCliAsync(params string[] args)
    {
        var project = Path.Combine(RepoRoot.Path, "src", "PCBHelper.Cli", "PCBHelper.Cli.csproj");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoRoot.Path
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(project);
        startInfo.ArgumentList.Add("--");
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start dotnet.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
