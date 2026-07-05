using System.Diagnostics;
using System.IO.Compression;
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
    public async Task Tutorial_Fixture_Exports_And_Packages_Through_Cli_Boundary()
    {
        var locator = new KiCadCliLocator();
        var location = locator.Locate();
        if (!location.Found)
        {
            _output.WriteLine("Skipped KiCad-dependent E2E: kicad-cli was not found.");
            return;
        }

        using var fixture = TestFixture.CopyTutorialBoard();

        var summary = await RunCliAsync("summary", fixture.Path, "--json");
        Assert.Equal(0, summary.ExitCode);

        var boardSummary = await RunCliAsync("board-summary", fixture.Path, "--json");
        Assert.Equal(0, boardSummary.ExitCode);

        var check = await RunCliAsync("check", fixture.Path, "--json");
        Assert.Equal(0, check.ExitCode);

        var export = await RunCliAsync("export", fixture.Path, "--json");
        Assert.Equal(0, export.ExitCode);
        using (var exportDocument = JsonDocument.Parse(export.StandardOutput))
        {
            var generatedFiles = exportDocument.RootElement.GetProperty("data").GetProperty("generatedFiles");
            Assert.Contains(generatedFiles.EnumerateArray(), static item => item.GetString()?.EndsWith(".gm1", StringComparison.OrdinalIgnoreCase) == true);
            Assert.Contains(generatedFiles.EnumerateArray(), static item => item.GetString()?.EndsWith(".drl", StringComparison.OrdinalIgnoreCase) == true);
        }

        var package = await RunCliAsync("package", fixture.Path, "--json");
        Assert.Equal(0, package.ExitCode);
        using var packageDocument = JsonDocument.Parse(package.StandardOutput);
        var zipPath = packageDocument.RootElement.GetProperty("data").GetProperty("zipPath").GetString();
        Assert.True(File.Exists(zipPath));

        using var archive = ZipFile.OpenRead(zipPath!);
        Assert.Contains(archive.Entries, static entry => entry.FullName == "manifest.json");
        Assert.Contains(archive.Entries, static entry => entry.FullName.EndsWith(".gm1", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(archive.Entries, static entry => entry.FullName.EndsWith(".drl", StringComparison.OrdinalIgnoreCase));
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

        using var fixture = TestFixture.CopyMinimalBoard();
        var summary = await RunCliAsync("summary", fixture.Path, "--json");
        Assert.Equal(0, summary.ExitCode);

        var check = await RunCliAsync("check", fixture.Path, "--json");
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
