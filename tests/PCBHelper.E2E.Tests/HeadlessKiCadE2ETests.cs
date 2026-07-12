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
    public async Task Blank_Project_Can_Author_Schematic_Update_Pcb_And_Package()
    {
        var locator = new KiCadCliLocator();
        var location = locator.Locate();
        if (!location.Found)
        {
            _output.WriteLine("Skipped KiCad-dependent E2E: kicad-cli was not found.");
            return;
        }

        using var fixture = TestFixture.CopyBlankAuthoring();

        Assert.Equal(0, (await RunCliAsync("create-schematic-symbol", fixture.Path, "--symbol", "Device:Battery_Cell", "--ref", "BT1", "--x", "30", "--y", "50", "--json")).ExitCode);
        Assert.Equal(0, (await RunCliAsync("create-schematic-symbol", fixture.Path, "--symbol", "Device:R", "--ref", "R1", "--x", "50", "--y", "50", "--value", "330R", "--json")).ExitCode);
        Assert.Equal(0, (await RunCliAsync("create-schematic-symbol", fixture.Path, "--symbol", "Device:LED", "--ref", "D1", "--x", "70", "--y", "50", "--json")).ExitCode);
        Assert.Equal(0, (await RunCliAsync("set-symbol-field", fixture.Path, "--ref", "R1", "--field", "Footprint", "--value", "R_Axial_2Pad", "--json")).ExitCode);
        Assert.Equal(0, (await RunCliAsync("connect-schematic-pins", fixture.Path, "--from", "BT1.+", "--to", "R1.1", "--net", "VCC", "--json")).ExitCode);
        Assert.Equal(0, (await RunCliAsync("connect-schematic-pins", fixture.Path, "--from", "R1.2", "--to", "D1.A", "--net", "LED_A", "--json")).ExitCode);
        Assert.Equal(0, (await RunCliAsync("connect-schematic-pins", fixture.Path, "--from", "D1.K", "--to", "BT1.-", "--net", "GND", "--json")).ExitCode);

        var schematic = await RunCliAsync("list-schematic-symbols", fixture.Path, "--json");
        Assert.Equal(0, schematic.ExitCode);
        using (var document = JsonDocument.Parse(schematic.StandardOutput))
        {
            var data = document.RootElement.GetProperty("data");
            Assert.Equal(3, data.GetProperty("symbols").GetArrayLength());
            Assert.True(data.GetProperty("wireCount").GetInt32() >= 3);
            Assert.True(data.GetProperty("labelCount").GetInt32() >= 3);
        }

        var update = await RunCliAsync("update-pcb-from-schematic", fixture.Path, "--json");
        Assert.Equal(0, update.ExitCode);
        using (var document = JsonDocument.Parse(update.StandardOutput))
        {
            var data = document.RootElement.GetProperty("data");
            Assert.True(File.Exists(data.GetProperty("changeReportPath").GetString()));
        }

        var board = await RunCliAsync("board-summary", fixture.Path, "--json");
        Assert.Equal(0, board.ExitCode);
        using (var document = JsonDocument.Parse(board.StandardOutput))
        {
            var references = document.RootElement.GetProperty("data").GetProperty("footprints")
                .EnumerateArray()
                .Select(static item => item.GetProperty("reference").GetString())
                .ToHashSet();

            Assert.Contains("BT1", references);
            Assert.Contains("R1", references);
            Assert.Contains("D1", references);
        }

        var nets = await RunCliAsync("list-nets", fixture.Path, "--json");
        Assert.Equal(0, nets.ExitCode);
        using (var document = JsonDocument.Parse(nets.StandardOutput))
        {
            var names = document.RootElement.GetProperty("data").GetProperty("nets")
                .EnumerateArray()
                .Select(static item => item.GetProperty("name").GetString())
                .ToHashSet();

            Assert.Contains("VCC", names);
            Assert.Contains("LED_A", names);
            Assert.Contains("GND", names);
        }

        var extraLabel = await RunCliAsync("add-net-label", fixture.Path, "--net", "EXTRA", "--x", "85", "--y", "60", "--json");
        Assert.Equal(0, extraLabel.ExitCode);
        string labelChange;
        using (var document = JsonDocument.Parse(extraLabel.StandardOutput))
        {
            labelChange = document.RootElement.GetProperty("data").GetProperty("changeReportPath").GetString()!;
            Assert.True(File.Exists(labelChange));
        }

        var restore = await RunCliAsync("restore-change", fixture.Path, "--change", labelChange, "--json");
        Assert.Equal(0, restore.ExitCode);
        var restoredSchematic = await RunCliAsync("list-schematic-symbols", fixture.Path, "--json");
        Assert.Equal(0, restoredSchematic.ExitCode);
        using (var document = JsonDocument.Parse(restoredSchematic.StandardOutput))
        {
            Assert.Equal(3, document.RootElement.GetProperty("data").GetProperty("labelCount").GetInt32());
        }

        Assert.Equal(0, (await RunCliAsync("check", fixture.Path, "--json")).ExitCode);
        Assert.Equal(0, (await RunCliAsync("export", fixture.Path, "--json")).ExitCode);
        Assert.Equal(0, (await RunCliAsync("package", fixture.Path, "--json")).ExitCode);
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

    [Fact]
    public async Task Simulation_Assertion_Fixture_Evaluates_External_Measurements()
    {
        using var fixture = TestFixture.CopySimulationAssertions();
        var passResults = Path.Combine(fixture.Path, "measurements-pass.json");
        var failResults = Path.Combine(fixture.Path, "measurements-fail.json");

        var validate = await RunCliAsync("validate-tests", fixture.Path, "--json");
        Assert.Equal(0, validate.ExitCode);

        var pass = await RunCliAsync("evaluate-test-results", fixture.Path, "--results", passResults, "--json");
        Assert.Equal(0, pass.ExitCode);
        using (var document = JsonDocument.Parse(pass.StandardOutput))
        {
            Assert.True(document.RootElement.GetProperty("data").GetProperty("passed").GetBoolean());
        }

        var fail = await RunCliAsync("evaluate-test-results", fixture.Path, "--results", failResults, "--json");
        Assert.NotEqual(0, fail.ExitCode);
        using (var document = JsonDocument.Parse(fail.StandardOutput))
        {
            var root = document.RootElement;
            Assert.Equal("TEST_ASSERTIONS_FAILED", root.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(1, root.GetProperty("data").GetProperty("failedAssertionCount").GetInt32());
            Assert.Contains(
                root.GetProperty("data").GetProperty("tests").EnumerateArray()
                    .SelectMany(test => test.GetProperty("assertions").EnumerateArray()),
                assertion => assertion.GetProperty("measurement").GetString() == "gain_at_10khz_db"
                    && assertion.GetProperty("passed").GetBoolean() == false);
        }
    }

    [Fact]
    public async Task Design_Plan_Previews_Applies_And_Restores_Through_Cli()
    {
        using var fixture = TestFixture.CopyTutorialBoard();
        var planPath = Path.Combine(fixture.Path, "plan.json");
        await File.WriteAllTextAsync(planPath, """{"version":1,"goal":"Change R1","operations":[{"id":"value","type":"set-component-value","reference":"R1","value":"300R"}],"engineeringGate":{"erc":"skip","drc":"skip","manufacturingValidation":"skip"}}""");
        var preview = await RunCliAsync("plan", "preview", fixture.Path, "--file", planPath, "--json");
        Assert.Equal(0, preview.ExitCode);
        using var previewJson = JsonDocument.Parse(preview.StandardOutput);
        var data = previewJson.RootElement.GetProperty("data");
        var hash = data.GetProperty("planHash").GetString()!;
        var decisions = string.Join(',', data.GetProperty("requiredDecisions").EnumerateArray().Select(static item => item.GetProperty("decisionId").GetString()));

        var apply = await RunCliAsync("plan", "apply", fixture.Path, "--file", planPath, "--expected-hash", hash, "--acknowledged-decisions", decisions, "--json");
        Assert.Equal(0, apply.ExitCode);
        using var applyJson = JsonDocument.Parse(apply.StandardOutput);
        var transactionId = applyJson.RootElement.GetProperty("data").GetProperty("transaction").GetProperty("transaction").GetProperty("transactionId").GetString()!;

        var value = await RunCliAsync("get-value", fixture.Path, "--ref", "R1", "--json");
        Assert.Contains("300R", value.StandardOutput);
        var restore = await RunCliAsync("transaction", "restore", fixture.Path, "--id", transactionId, "--json");
        Assert.Equal(0, restore.ExitCode);
        var restoredValue = await RunCliAsync("get-value", fixture.Path, "--ref", "R1", "--json");
        Assert.Contains("330R", restoredValue.StandardOutput);
    }

    [Fact]
    public async Task Workflow_Generates_Visual_Review_And_Gated_PcbWay_Package()
    {
        using var fixture = TestFixture.CopyTutorialBoard();
        var runtime = PCBHelperRuntime.ForCli();
        Assert.True(new KiCadCliLocator().Locate().Found, "Workflow release E2E requires KiCad; it must not pass by returning early.");

        var review = await runtime.Workflows.GenerateReviewPackageAsync(fixture.Path);
        Assert.True(review.Success, review.Error?.Message);
        Assert.True(File.Exists(review.Data!.ReportPath));
        Assert.Contains(review.Data.RenderFiles, static path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(path));
        Assert.Contains(review.Data.RenderFiles, static path => path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) && File.Exists(path));

        var plan = """{"version":1,"goal":"Prepare release transaction","operations":[{"id":"value","type":"set-component-value","reference":"R1","value":"300R"}],"engineeringGate":{"erc":"skip","drc":"skip","manufacturingValidation":"skip"}}""";
        var preview = runtime.Plans.Preview(fixture.Path, plan);
        var applied = await runtime.Plans.ApplyAsync(fixture.Path, plan, preview.Data!.PlanHash,
            preview.Data.RequiredDecisions.Select(static decision => decision.DecisionId).ToArray());
        Assert.True(applied.Success, applied.Error?.Message);

        var package = await runtime.Workflows.GeneratePcbWayPackageAsync(fixture.Path);
        Assert.True(package.Success, package.Error?.Message);
        Assert.True(File.Exists(package.Data!.ZipPath));
    }

    [Fact]
    public async Task Ngspice_Fixture_Validates_And_Never_Passes_When_Backend_Is_Missing()
    {
        using var fixture = TestFixture.CopyNgspiceSimulation();
        var validation = await RunCliAsync("simulation", "validate", fixture.Path, "--json");
        Assert.Equal(0, validation.ExitCode);

        var status = await RunCliAsync("simulation", "status", "--json");
        using var statusDocument = JsonDocument.Parse(status.StandardOutput);
        var available = statusDocument.RootElement.GetProperty("data").GetProperty("available").GetBoolean();
        var run = await RunCliAsync("simulation", "run", fixture.Path, "--json");
        using var runDocument = JsonDocument.Parse(run.StandardOutput);
        if (available)
        {
            Assert.Equal(0, run.ExitCode);
            Assert.True(runDocument.RootElement.GetProperty("data").GetProperty("passed").GetBoolean());
        }
        else
        {
            Assert.NotEqual(0, run.ExitCode);
            Assert.False(runDocument.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("SIMULATOR_NOT_FOUND", runDocument.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
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
