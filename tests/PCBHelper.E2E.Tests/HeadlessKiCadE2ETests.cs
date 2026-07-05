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
    public async Task Tutorial_Fixture_Measures_Moves_And_Still_Packages()
    {
        var locator = new KiCadCliLocator();
        var location = locator.Locate();
        if (!location.Found)
        {
            _output.WriteLine("Skipped KiCad-dependent E2E: kicad-cli was not found.");
            return;
        }

        using var fixture = TestFixture.CopyTutorialBoard();

        var boardSummary = await RunCliAsync("board-summary", fixture.Path, "--json");
        Assert.Equal(0, boardSummary.ExitCode);

        var measure = await RunCliAsync("measure", fixture.Path, "--from", "R1", "--to", "D1", "--json");
        Assert.Equal(0, measure.ExitCode);

        var dryRun = await RunCliAsync("move", fixture.Path, "--ref", "D1", "--x", "75", "--y", "35", "--dry-run", "--json");
        Assert.Equal(0, dryRun.ExitCode);

        var spacing = await RunCliAsync("set-spacing", fixture.Path, "--fixed", "R1", "--moving", "D1", "--distance", "25", "--axis", "x", "--json");
        Assert.Equal(0, spacing.ExitCode);
        string changeReportPath;
        using (var spacingDocument = JsonDocument.Parse(spacing.StandardOutput))
        {
            var data = spacingDocument.RootElement.GetProperty("data");
            changeReportPath = data.GetProperty("changeReportPath").GetString()!;
            Assert.True(File.Exists(changeReportPath));
            Assert.NotEmpty(data.GetProperty("checkReportPaths").EnumerateArray());
        }

        var movedSummary = await RunCliAsync("board-summary", fixture.Path, "--json");
        Assert.Equal(0, movedSummary.ExitCode);
        using (var document = JsonDocument.Parse(movedSummary.StandardOutput))
        {
            var d1 = document.RootElement.GetProperty("data").GetProperty("footprints")
                .EnumerateArray()
                .Single(item => item.GetProperty("reference").GetString() == "D1");
            Assert.Equal(70, d1.GetProperty("xMillimeters").GetDouble(), precision: 3);
            Assert.Equal(35, d1.GetProperty("yMillimeters").GetDouble(), precision: 3);
        }

        var restore = await RunCliAsync("restore-change", fixture.Path, "--change", changeReportPath, "--json");
        Assert.Equal(0, restore.ExitCode);

        var restoredSummary = await RunCliAsync("board-summary", fixture.Path, "--json");
        Assert.Equal(0, restoredSummary.ExitCode);
        using (var document = JsonDocument.Parse(restoredSummary.StandardOutput))
        {
            var d1 = document.RootElement.GetProperty("data").GetProperty("footprints")
                .EnumerateArray()
                .Single(item => item.GetProperty("reference").GetString() == "D1");
            Assert.Equal(68, d1.GetProperty("xMillimeters").GetDouble(), precision: 3);
            Assert.Equal(35, d1.GetProperty("yMillimeters").GetDouble(), precision: 3);
        }

        var check = await RunCliAsync("check", fixture.Path, "--json");
        Assert.Equal(0, check.ExitCode);

        var export = await RunCliAsync("export", fixture.Path, "--json");
        Assert.Equal(0, export.ExitCode);

        var package = await RunCliAsync("package", fixture.Path, "--json");
        Assert.Equal(0, package.ExitCode);
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
