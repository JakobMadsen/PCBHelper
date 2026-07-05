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
        double originalD1X;
        double originalD1Y;
        using (var document = JsonDocument.Parse(boardSummary.StandardOutput))
        {
            var d1 = document.RootElement.GetProperty("data").GetProperty("footprints")
                .EnumerateArray()
                .Single(item => item.GetProperty("reference").GetString() == "D1");
            originalD1X = d1.GetProperty("xMillimeters").GetDouble();
            originalD1Y = d1.GetProperty("yMillimeters").GetDouble();
        }

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
            Assert.Equal(originalD1X, d1.GetProperty("xMillimeters").GetDouble(), precision: 3);
            Assert.Equal(originalD1Y, d1.GetProperty("yMillimeters").GetDouble(), precision: 3);
        }

        var check = await RunCliAsync("check", fixture.Path, "--json");
        Assert.Equal(0, check.ExitCode);

        var export = await RunCliAsync("export", fixture.Path, "--json");
        Assert.Equal(0, export.ExitCode);

        var package = await RunCliAsync("package", fixture.Path, "--json");
        Assert.Equal(0, package.ExitCode);
    }

    [Fact]
    public async Task Tutorial_Fixture_Changes_Value_Exports_And_Restores_Through_Cli_Boundary()
    {
        var locator = new KiCadCliLocator();
        var location = locator.Locate();
        if (!location.Found)
        {
            _output.WriteLine("Skipped KiCad-dependent E2E: kicad-cli was not found.");
            return;
        }

        using var fixture = TestFixture.CopyTutorialBoard();

        var components = await RunCliAsync("list-components", fixture.Path, "--json");
        Assert.Equal(0, components.ExitCode);

        var before = await RunCliAsync("get-value", fixture.Path, "--ref", "R1", "--json");
        Assert.Equal(0, before.ExitCode);
        using (var document = JsonDocument.Parse(before.StandardOutput))
        {
            var locations = document.RootElement.GetProperty("data").GetProperty("locations");
            Assert.Contains(locations.EnumerateArray(), static item => item.GetProperty("value").GetString() == "330R");
        }

        var set = await RunCliAsync("set-value", fixture.Path, "--ref", "R1", "--value", "300R", "--json");
        Assert.Equal(0, set.ExitCode);
        string changeReportPath;
        using (var document = JsonDocument.Parse(set.StandardOutput))
        {
            var data = document.RootElement.GetProperty("data");
            changeReportPath = data.GetProperty("changeReportPath").GetString()!;
            Assert.True(File.Exists(changeReportPath));
        }

        var changed = await RunCliAsync("get-value", fixture.Path, "--ref", "R1", "--json");
        Assert.Equal(0, changed.ExitCode);
        using (var document = JsonDocument.Parse(changed.StandardOutput))
        {
            Assert.Contains(
                document.RootElement.GetProperty("data").GetProperty("locations").EnumerateArray(),
                static item => item.GetProperty("value").GetString() == "300R");
        }

        var check = await RunCliAsync("check", fixture.Path, "--json");
        Assert.Equal(0, check.ExitCode);

        var export = await RunCliAsync("export", fixture.Path, "--json");
        Assert.Equal(0, export.ExitCode);

        var package = await RunCliAsync("package", fixture.Path, "--json");
        Assert.Equal(0, package.ExitCode);

        var restore = await RunCliAsync("restore-change", fixture.Path, "--change", changeReportPath, "--json");
        Assert.Equal(0, restore.ExitCode);

        var restored = await RunCliAsync("get-value", fixture.Path, "--ref", "R1", "--json");
        Assert.Equal(0, restored.ExitCode);
        using (var document = JsonDocument.Parse(restored.StandardOutput))
        {
            Assert.Contains(
                document.RootElement.GetProperty("data").GetProperty("locations").EnumerateArray(),
                static item => item.GetProperty("value").GetString() == "330R");
        }
    }

    [Fact]
    public async Task Tutorial_Fixture_Routing_Primitives_Restore_And_Still_Package()
    {
        var locator = new KiCadCliLocator();
        var location = locator.Locate();
        if (!location.Found)
        {
            _output.WriteLine("Skipped KiCad-dependent E2E: kicad-cli was not found.");
            return;
        }

        using var fixture = TestFixture.CopyTutorialBoard();

        var tracks = await RunCliAsync("list-tracks", fixture.Path, "--json");
        Assert.Equal(0, tracks.ExitCode);
        int initialTrackCount;
        using (var document = JsonDocument.Parse(tracks.StandardOutput))
        {
            initialTrackCount = document.RootElement.GetProperty("data").GetProperty("tracks").GetArrayLength();
        }

        var vias = await RunCliAsync("list-vias", fixture.Path, "--json");
        Assert.Equal(0, vias.ExitCode);
        int initialViaCount;
        using (var document = JsonDocument.Parse(vias.StandardOutput))
        {
            initialViaCount = document.RootElement.GetProperty("data").GetProperty("vias").GetArrayLength();
        }

        var routing = await RunCliAsync("get-net-routing", fixture.Path, "--net", "LED_A", "--json");
        Assert.Equal(0, routing.ExitCode);

        var previewTrack = await RunCliAsync("add-track", fixture.Path, "--net", "LED_A", "--start-x", "10", "--start-y", "10", "--end-x", "20", "--end-y", "10", "--layer", "F.Cu", "--width", "0.25", "--dry-run", "--json");
        Assert.Equal(0, previewTrack.ExitCode);

        var addTrack = await RunCliAsync("add-track", fixture.Path, "--net", "LED_A", "--start-x", "10", "--start-y", "10", "--end-x", "20", "--end-y", "10", "--layer", "F.Cu", "--width", "0.25", "--json");
        Assert.Equal(0, addTrack.ExitCode);
        string trackChange;
        using (var document = JsonDocument.Parse(addTrack.StandardOutput))
        {
            trackChange = document.RootElement.GetProperty("data").GetProperty("changeReportPath").GetString()!;
            Assert.True(File.Exists(trackChange));
        }

        var afterTrackAdd = await RunCliAsync("list-tracks", fixture.Path, "--json");
        using (var document = JsonDocument.Parse(afterTrackAdd.StandardOutput))
        {
            Assert.Equal(initialTrackCount + 1, document.RootElement.GetProperty("data").GetProperty("tracks").GetArrayLength());
        }

        var restoreTrack = await RunCliAsync("restore-change", fixture.Path, "--change", trackChange, "--json");
        Assert.Equal(0, restoreTrack.ExitCode);
        var afterTrackRestore = await RunCliAsync("list-tracks", fixture.Path, "--json");
        using (var document = JsonDocument.Parse(afterTrackRestore.StandardOutput))
        {
            Assert.Equal(initialTrackCount, document.RootElement.GetProperty("data").GetProperty("tracks").GetArrayLength());
        }

        var previewVia = await RunCliAsync("add-via", fixture.Path, "--net", "GND", "--x", "73", "--y", "45", "--size", "1.2", "--drill", "0.6", "--layers", "F.Cu,B.Cu", "--dry-run", "--json");
        Assert.Equal(0, previewVia.ExitCode);

        var addVia = await RunCliAsync("add-via", fixture.Path, "--net", "GND", "--x", "73", "--y", "45", "--size", "1.2", "--drill", "0.6", "--layers", "F.Cu,B.Cu", "--json");
        Assert.Equal(0, addVia.ExitCode);
        string viaChange;
        using (var document = JsonDocument.Parse(addVia.StandardOutput))
        {
            viaChange = document.RootElement.GetProperty("data").GetProperty("changeReportPath").GetString()!;
            Assert.True(File.Exists(viaChange));
        }

        var afterViaAdd = await RunCliAsync("list-vias", fixture.Path, "--json");
        using (var document = JsonDocument.Parse(afterViaAdd.StandardOutput))
        {
            Assert.Equal(initialViaCount + 1, document.RootElement.GetProperty("data").GetProperty("vias").GetArrayLength());
        }

        var restoreVia = await RunCliAsync("restore-change", fixture.Path, "--change", viaChange, "--json");
        Assert.Equal(0, restoreVia.ExitCode);
        var afterViaRestore = await RunCliAsync("list-vias", fixture.Path, "--json");
        using (var document = JsonDocument.Parse(afterViaRestore.StandardOutput))
        {
            Assert.Equal(initialViaCount, document.RootElement.GetProperty("data").GetProperty("vias").GetArrayLength());
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
