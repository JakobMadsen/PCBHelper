using System.Diagnostics;
using System.Text.Json;
using PCBHelper.Core;
using Xunit.Abstractions;

namespace PCBHelper.E2E.Tests;

public sealed class HeadlessKiCadE2ETests
{
    private readonly ITestOutputHelper _output;

    public HeadlessKiCadE2ETests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Doctor_Summary_Check_Run_Through_Cli_Boundary()
    {
        var locator = new KiCadCliLocator();
        var location = locator.Locate();
        if (!location.Found)
        {
            _output.WriteLine("Skipped KiCad-dependent E2E: kicad-cli was not found.");
            return;
        }

        var doctor = await RunCliAsync("doctor", "--json");
        Assert.Equal(0, doctor.ExitCode);

        var fixture = Path.Combine(RepoRoot.Path, "fixtures", "minimal-board");
        var summary = await RunCliAsync("summary", fixture, "--json");
        Assert.Equal(0, summary.ExitCode);

        var check = await RunCliAsync("check", fixture, "--json");
        using var document = JsonDocument.Parse(check.StandardOutput);
        var data = document.RootElement.GetProperty("data");
        if (data.ValueKind == JsonValueKind.Null)
        {
            _output.WriteLine(check.StandardOutput);
            _output.WriteLine(check.StandardError);
            Assert.Fail("Expected check data from KiCad-dependent E2E.");
        }

        var checks = data.GetProperty("checks");
        Assert.NotEmpty(checks.EnumerateArray());
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
