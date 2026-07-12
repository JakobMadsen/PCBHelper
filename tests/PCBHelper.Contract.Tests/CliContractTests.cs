using System.Diagnostics;
using System.Text.Json;

namespace PCBHelper.Contract.Tests;

public sealed class CliContractTests
{
    [Fact]
    public async Task DesignPlan_Validate_And_Preview_Return_Stable_Contracts()
    {
        using var fixture = TestFixture.CopyTutorialBoard();
        var planPath = Path.Combine(fixture.Path, "plan.json");
        await File.WriteAllTextAsync(planPath, """{"version":1,"goal":"Change R1","operations":[{"id":"value","type":"set-component-value","reference":"R1","value":"300R"}]}""");

        var validated = await RunCliAsync("plan", "validate", fixture.Path, "--file", planPath, "--json");
        var previewed = await RunCliAsync("plan", "preview", fixture.Path, "--file", planPath, "--json");

        Assert.Equal(0, validated.ExitCode);
        Assert.Equal(0, previewed.ExitCode);
        using var validationJson = JsonDocument.Parse(validated.StandardOutput);
        using var previewJson = JsonDocument.Parse(previewed.StandardOutput);
        Assert.Equal(64, validationJson.RootElement.GetProperty("data").GetProperty("planHash").GetString()!.Length);
        Assert.True(previewJson.RootElement.GetProperty("data").GetProperty("changedFiles").GetArrayLength() > 0);
    }

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
        using var fixture = TestFixture.CopyMinimalBoard();
        var result = await RunCliAsync("summary", fixture.Path, "--json");

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
    public async Task Check_Json_Returns_Stable_Envelope()
    {
        using var fixture = TestFixture.CopyMinimalBoard();
        var result = await RunCliAsync("check", fixture.Path, "--json");

        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput), result.StandardError);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("success", out _));
        Assert.True(root.TryGetProperty("summary", out _));
        Assert.True(root.TryGetProperty("data", out _));
        Assert.True(root.TryGetProperty("warnings", out _));
        Assert.True(root.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task BoardSummary_Json_Returns_Tutorial_Footprints()
    {
        using var fixture = TestFixture.CopyTutorialBoard();
        var result = await RunCliAsync("board-summary", fixture.Path, "--json");

        Assert.Equal(0, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var footprints = document.RootElement.GetProperty("data").GetProperty("footprints");
        var references = footprints.EnumerateArray()
            .Select(static item => item.GetProperty("reference").GetString())
            .ToHashSet();

        Assert.Contains("BT1", references);
        Assert.Contains("R1", references);
        Assert.Contains("D1", references);
    }

    [Theory]
    [InlineData("export")]
    [InlineData("package")]
    [InlineData("package-assembly")]
    public async Task Manufacturing_Commands_Return_Stable_Json_Envelope(string command)
    {
        using var fixture = TestFixture.CopyTutorialBoard();
        var result = await RunCliAsync(command, fixture.Path, "--json");

        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput), result.StandardError);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("success", out _));
        Assert.True(root.TryGetProperty("summary", out _));
        Assert.True(root.TryGetProperty("data", out _));
        Assert.True(root.TryGetProperty("warnings", out _));
        Assert.True(root.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Measure_Json_Returns_Distance_Data()
    {
        using var fixture = TestFixture.CopyTutorialBoard();
        var result = await RunCliAsync("measure", fixture.Path, "--from", "R1", "--to", "D1", "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var data = document.RootElement.GetProperty("data");

        Assert.Equal("R1", data.GetProperty("fromReference").GetString());
        Assert.Equal("D1", data.GetProperty("toReference").GetString());
        Assert.True(data.GetProperty("distanceMillimeters").GetDouble() > 0);
    }

    [Fact]
    public async Task Move_DryRun_Json_Returns_Stable_Envelope()
    {
        using var fixture = TestFixture.CopyTutorialBoard();
        var result = await RunCliAsync("move", fixture.Path, "--ref", "D1", "--x", "75", "--y", "35", "--dry-run", "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("data").GetProperty("dryRun").GetBoolean());
        Assert.Equal(75, root.GetProperty("data").GetProperty("after").GetProperty("xMillimeters").GetDouble(), precision: 3);
    }

    [Fact]
    public async Task Move_Json_Returns_Stable_Envelope()
    {
        using var fixture = TestFixture.CopyTutorialBoard();
        var result = await RunCliAsync("move", fixture.Path, "--ref", "D1", "--x", "75", "--y", "35", "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.False(root.GetProperty("data").GetProperty("dryRun").GetBoolean());
        Assert.True(File.Exists(root.GetProperty("data").GetProperty("changeReportPath").GetString()));
        Assert.True(root.GetProperty("data").TryGetProperty("checkSummary", out _));
    }

    [Fact]
    public async Task SetSpacing_Json_Returns_Stable_Envelope()
    {
        using var fixture = TestFixture.CopyTutorialBoard();
        var result = await RunCliAsync("set-spacing", fixture.Path, "--fixed", "R1", "--moving", "D1", "--distance", "25", "--axis", "x", "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var data = document.RootElement.GetProperty("data");

        Assert.Equal("R1", data.GetProperty("fixedReference").GetString());
        Assert.Equal("D1", data.GetProperty("movingReference").GetString());
        Assert.Equal("x", data.GetProperty("axis").GetString());
        Assert.True(File.Exists(data.GetProperty("changeReportPath").GetString()));
    }

    [Fact]
    public async Task RestoreChange_DryRun_Json_Returns_Stable_Envelope()
    {
        using var fixture = TestFixture.CopyTutorialBoard();
        var move = await RunCliAsync("move", fixture.Path, "--ref", "D1", "--x", "75", "--y", "35", "--json");
        Assert.Equal(0, move.ExitCode);
        using var moveDocument = JsonDocument.Parse(move.StandardOutput);
        var changeReportPath = moveDocument.RootElement.GetProperty("data").GetProperty("changeReportPath").GetString();

        var restore = await RunCliAsync("restore-change", fixture.Path, "--change", changeReportPath!, "--dry-run", "--json");

        Assert.Equal(0, restore.ExitCode);
        using var restoreDocument = JsonDocument.Parse(restore.StandardOutput);
        var data = restoreDocument.RootElement.GetProperty("data");

        Assert.True(data.GetProperty("dryRun").GetBoolean());
        Assert.Equal("D1", data.GetProperty("reference").GetString());
        Assert.True(data.GetProperty("after").TryGetProperty("xMillimeters", out _));
    }

    [Fact]
    public async Task Open_DryRun_Json_Returns_Stable_Envelope()
    {
        using var fixture = TestFixture.CopyTutorialBoard();
        var result = await RunCliAsync("open", fixture.Path, "--dry-run", "--json");

        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput), result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("success", out _));
        Assert.True(root.TryGetProperty("summary", out _));
        Assert.True(root.TryGetProperty("data", out _));
        Assert.True(root.TryGetProperty("warnings", out _));
        Assert.True(root.TryGetProperty("error", out _));
    }

    [Theory]
    [InlineData("list-components")]
    [InlineData("list-nets")]
    [InlineData("list-tracks")]
    [InlineData("list-vias")]
    [InlineData("list-unrouted-connections")]
    [InlineData("list-schematic-symbols")]
    [InlineData("list-tests")]
    [InlineData("validate-tests")]
    [InlineData("check-summary")]
    [InlineData("export-bom")]
    [InlineData("export-position-files")]
    [InlineData("export-assembly-bom")]
    [InlineData("export-cpl")]
    [InlineData("validate-assembly-package")]
    [InlineData("kicad-gui-status")]
    public async Task New_Project_Commands_Return_Stable_Json_Envelope(string command)
    {
        using var fixture = TestFixture.CopyTutorialBoard();
        var result = await RunCliAsync(command, fixture.Path, "--json");

        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput), result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("success", out _));
        Assert.True(root.TryGetProperty("summary", out _));
        Assert.True(root.TryGetProperty("data", out _));
        Assert.True(root.TryGetProperty("warnings", out _));
        Assert.True(root.TryGetProperty("error", out _));
    }

    [Theory]
    [InlineData("get-value", "--ref", "R1")]
    [InlineData("set-value", "--ref", "R1", "--value", "300R", "--dry-run")]
    [InlineData("get-net", "--net", "LED_A")]
    [InlineData("list-footprint-pads", "--ref", "R1")]
    [InlineData("get-net-routing", "--net", "LED_A")]
    [InlineData("validate-track-clearance", "--net", "LED_A", "--points", "10,10;20,10", "--layer", "F.Cu", "--width", "0.25")]
    [InlineData("add-track", "--net", "LED_A", "--start-x", "10", "--start-y", "10", "--end-x", "20", "--end-y", "10", "--layer", "F.Cu", "--width", "0.25", "--dry-run")]
    [InlineData("add-track-polyline", "--net", "LED_A", "--points", "10,10;20,10;20,20", "--layer", "F.Cu", "--width", "0.25", "--dry-run")]
    [InlineData("add-via", "--net", "GND", "--x", "30", "--y", "30", "--size", "1.2", "--drill", "0.6", "--layers", "F.Cu,B.Cu", "--dry-run")]
    [InlineData("create-schematic-symbol", "--symbol", "Device:R", "--ref", "R99", "--x", "50", "--y", "50", "--value", "330R", "--dry-run")]
    [InlineData("add-net-label", "--net", "VCC", "--x", "40", "--y", "50", "--dry-run")]
    [InlineData("update-pcb-from-schematic", "--dry-run")]
    [InlineData("autoroute-board", "--dry-run")]
    [InlineData("focus-component", "--ref", "R1")]
    public async Task New_Option_Commands_Return_Stable_Json_Envelope(string command, params string[] commandArgs)
    {
        using var fixture = TestFixture.CopyTutorialBoard();
        var args = new List<string> { command, fixture.Path };
        args.AddRange(commandArgs);
        args.Add("--json");

        var result = await RunCliAsync(args.ToArray());

        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput), result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("success", out _));
        Assert.True(root.TryGetProperty("summary", out _));
        Assert.True(root.TryGetProperty("data", out _));
        Assert.True(root.TryGetProperty("warnings", out _));
        Assert.True(root.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Schematic_Authoring_Commands_Return_Stable_Json_Envelope()
    {
        using var fixture = TestFixture.CopyBlankAuthoring();

        var create = await RunCliAsync("create-schematic-symbol", fixture.Path, "--symbol", "Device:R", "--ref", "R1", "--x", "50", "--y", "50", "--value", "330R", "--json");
        Assert.Equal(0, create.ExitCode);
        var createUnit = await RunCliAsync("create-schematic-symbol", fixture.Path, "--symbol", "Amplifier_Operational:OPA2325", "--ref", "U1", "--x", "60", "--y", "50", "--unit", "2", "--json");
        Assert.Equal(0, createUnit.ExitCode);

        foreach (var result in new[]
        {
            await RunCliAsync("list-schematic-symbols", fixture.Path, "--json"),
            await RunCliAsync("set-symbol-field", fixture.Path, "--ref", "R1", "--field", "Footprint", "--value", "R_Axial_2Pad", "--dry-run", "--json"),
            await RunCliAsync("connect-schematic-pins", fixture.Path, "--from", "R1.1", "--to", "R1.2", "--net", "LOOP", "--dry-run", "--json"),
            await RunCliAsync("connect-schematic-pins", fixture.Path, "--from", "R1:1", "--to", "R1:2", "--net", "LOOP", "--dry-run", "--json")
        })
        {
            Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput), result.StandardError);
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            Assert.True(root.TryGetProperty("success", out _));
            Assert.True(root.TryGetProperty("summary", out _));
            Assert.True(root.TryGetProperty("data", out _));
            Assert.True(root.TryGetProperty("warnings", out _));
            Assert.True(root.TryGetProperty("error", out _));
        }

        var list = await RunCliAsync("list-schematic-symbols", fixture.Path, "--json");
        using var listDocument = JsonDocument.Parse(list.StandardOutput);
        var symbols = listDocument.RootElement.GetProperty("data").GetProperty("symbols");
        Assert.Contains(symbols.EnumerateArray(), symbol => symbol.GetProperty("reference").GetString() == "U1" && symbol.GetProperty("unit").GetInt32() == 2);
    }

    [Fact]
    public async Task Test_Assertion_Commands_Return_Stable_Json_Envelope()
    {
        using var fixture = TestFixture.CopySimulationAssertions();
        var passResults = Path.Combine(fixture.Path, "measurements-pass.json");
        var failResults = Path.Combine(fixture.Path, "measurements-fail.json");

        foreach (var result in new[]
        {
            await RunCliAsync("list-tests", fixture.Path, "--json"),
            await RunCliAsync("validate-tests", fixture.Path, "--json"),
            await RunCliAsync("evaluate-test-results", fixture.Path, "--results", passResults, "--json")
        })
        {
            Assert.Equal(0, result.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput), result.StandardError);
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            Assert.True(root.TryGetProperty("success", out _));
            Assert.True(root.TryGetProperty("summary", out _));
            Assert.True(root.TryGetProperty("data", out _));
            Assert.True(root.TryGetProperty("warnings", out _));
            Assert.True(root.TryGetProperty("error", out _));
        }

        var failed = await RunCliAsync("evaluate-test-results", fixture.Path, "--results", failResults, "--json");

        Assert.NotEqual(0, failed.ExitCode);
        using var failedDocument = JsonDocument.Parse(failed.StandardOutput);
        var failedRoot = failedDocument.RootElement;
        Assert.False(failedRoot.GetProperty("success").GetBoolean());
        Assert.Equal("TEST_ASSERTIONS_FAILED", failedRoot.GetProperty("error").GetProperty("code").GetString());
        Assert.False(failedRoot.GetProperty("data").GetProperty("passed").GetBoolean());
        Assert.Equal(1, failedRoot.GetProperty("data").GetProperty("failedAssertionCount").GetInt32());
    }

    [Fact]
    public async Task Simulation_Status_And_Validation_Return_Stable_Json_Envelopes()
    {
        using var fixture = TestFixture.CopyNgspiceSimulation();
        var status = await RunCliAsync("simulation", "status", "--json");
        using var statusDocument = JsonDocument.Parse(status.StandardOutput);
        Assert.True(statusDocument.RootElement.GetProperty("success").GetBoolean());
        Assert.True(statusDocument.RootElement.GetProperty("data").TryGetProperty("available", out _));

        var validation = await RunCliAsync("simulation", "validate", fixture.Path, "--json");
        Assert.Equal(0, validation.ExitCode);
        using var validationDocument = JsonDocument.Parse(validation.StandardOutput);
        Assert.True(validationDocument.RootElement.GetProperty("success").GetBoolean());
        Assert.True(validationDocument.RootElement.GetProperty("data").GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task RegenerateBoardFootprint_Command_Returns_Stable_Json_Envelope()
    {
        using var fixture = TestFixture.CopyBlankAuthoring();

        Assert.Equal(0, (await RunCliAsync("create-schematic-symbol", fixture.Path, "--symbol", "Device:LED", "--ref", "D1", "--x", "70", "--y", "50", "--json")).ExitCode);
        Assert.Equal(0, (await RunCliAsync("update-pcb-from-schematic", fixture.Path, "--json")).ExitCode);

        var result = await RunCliAsync("regenerate-board-footprint", fixture.Path, "--ref", "D1", "--dry-run", "--json");

        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput), result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("success", out _));
        Assert.True(root.TryGetProperty("summary", out _));
        Assert.True(root.TryGetProperty("data", out _));
        Assert.True(root.TryGetProperty("warnings", out _));
        Assert.True(root.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Schematic_Cleanup_Commands_Return_Stable_Json_Envelope()
    {
        using var fixture = TestFixture.CopyBlankAuthoring();

        Assert.Equal(0, (await RunCliAsync("create-schematic-symbol", fixture.Path, "--symbol", "Device:R", "--ref", "R1", "--x", "50", "--y", "50", "--json")).ExitCode);
        Assert.Equal(0, (await RunCliAsync("create-schematic-symbol", fixture.Path, "--symbol", "Device:LED", "--ref", "D1", "--x", "70", "--y", "50", "--json")).ExitCode);
        Assert.Equal(0, (await RunCliAsync("connect-schematic-pins", fixture.Path, "--from", "R1.2", "--to", "D1.A", "--net", "SIG", "--json")).ExitCode);
        Assert.Equal(0, (await RunCliAsync("add-net-label", fixture.Path, "--net", "EXTRA", "--x", "80", "--y", "50", "--json")).ExitCode);

        var list = await RunCliAsync("list-schematic-symbols", fixture.Path, "--json");
        using var listDocument = JsonDocument.Parse(list.StandardOutput);
        var data = listDocument.RootElement.GetProperty("data");
        var labelUuid = data.GetProperty("labels").EnumerateArray()
            .First(label => label.GetProperty("text").GetString() == "EXTRA")
            .GetProperty("uuid").GetString();
        var wireUuid = data.GetProperty("wires")[0].GetProperty("uuid").GetString();

        foreach (var result in new[]
        {
            await RunCliAsync("delete-net-label-by-uuid", fixture.Path, "--uuid", labelUuid!, "--dry-run", "--json"),
            await RunCliAsync("delete-net-label", fixture.Path, "--net", "EXTRA", "--x", "80", "--y", "50", "--dry-run", "--json"),
            await RunCliAsync("delete-schematic-wire-by-uuid", fixture.Path, "--uuid", wireUuid!, "--dry-run", "--json"),
            await RunCliAsync("delete-schematic-wire", fixture.Path, "--x1", "55.08", "--y1", "50", "--x2", "68.73", "--y2", "50", "--tolerance", "10", "--dry-run", "--json")
        })
        {
            Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput), result.StandardError);
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            Assert.True(root.TryGetProperty("success", out _));
            Assert.True(root.TryGetProperty("summary", out _));
            Assert.True(root.TryGetProperty("data", out _));
            Assert.True(root.TryGetProperty("warnings", out _));
            Assert.True(root.TryGetProperty("error", out _));
        }
    }

    [Fact]
    public async Task SetValue_Real_ListChanges_ShowChange_And_Restore_Return_Stable_Envelopes()
    {
        using var fixture = TestFixture.CopyTutorialBoard();
        var set = await RunCliAsync("set-value", fixture.Path, "--ref", "R1", "--value", "300R", "--json");

        Assert.Equal(0, set.ExitCode);
        using var setDocument = JsonDocument.Parse(set.StandardOutput);
        var changeReportPath = setDocument.RootElement.GetProperty("data").GetProperty("changeReportPath").GetString();
        Assert.True(File.Exists(changeReportPath));

        foreach (var result in new[]
        {
            await RunCliAsync("list-changes", fixture.Path, "--json"),
            await RunCliAsync("show-change", fixture.Path, "--change", changeReportPath!, "--json"),
            await RunCliAsync("restore-change", fixture.Path, "--change", changeReportPath!, "--json")
        })
        {
            Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput), result.StandardError);
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            Assert.True(root.TryGetProperty("success", out _));
            Assert.True(root.TryGetProperty("summary", out _));
            Assert.True(root.TryGetProperty("data", out _));
            Assert.True(root.TryGetProperty("warnings", out _));
            Assert.True(root.TryGetProperty("error", out _));
        }
    }

    [Fact]
    public async Task Routing_Delete_Commands_Return_Stable_Json_Envelope()
    {
        using var fixture = TestFixture.CopyTutorialBoard();
        var tracks = await RunCliAsync("list-tracks", fixture.Path, "--json");
        using var tracksDocument = JsonDocument.Parse(tracks.StandardOutput);
        var track = tracksDocument.RootElement.GetProperty("data").GetProperty("tracks")[0].GetProperty("id").GetString();

        var vias = await RunCliAsync("list-vias", fixture.Path, "--json");
        using var viasDocument = JsonDocument.Parse(vias.StandardOutput);
        var via = viasDocument.RootElement.GetProperty("data").GetProperty("vias")[0].GetProperty("id").GetString();

        foreach (var result in new[]
        {
            await RunCliAsync("delete-track", fixture.Path, "--track", track!, "--dry-run", "--json"),
            await RunCliAsync("delete-via", fixture.Path, "--via", via!, "--dry-run", "--json")
        })
        {
            Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput), result.StandardError);
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            Assert.True(root.TryGetProperty("success", out _));
            Assert.True(root.TryGetProperty("summary", out _));
            Assert.True(root.TryGetProperty("data", out _));
            Assert.True(root.TryGetProperty("warnings", out _));
            Assert.True(root.TryGetProperty("error", out _));
        }
    }

    private static async Task<ProcessResult> RunCliAsync(params string[] args)
    {
        var cli = GetBuiltCliPath();
        var commandText = $"dotnet {cli} {string.Join(' ', args)}";
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoRoot.Path
        };

        startInfo.ArgumentList.Add(cli);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start dotnet.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            var timedOutStdout = await stdout;
            var timedOutStderr = await stderr;
            Assert.Fail($"CLI command timed out after 60 seconds: {commandText}{Environment.NewLine}stdout tail:{Environment.NewLine}{Tail(timedOutStdout)}{Environment.NewLine}stderr tail:{Environment.NewLine}{Tail(timedOutStderr)}");
        }

        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private static string GetBuiltCliPath()
    {
        var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var framework = Path.GetFileName(baseDirectory);
        var configuration = Path.GetFileName(Path.GetDirectoryName(baseDirectory)!);
        var cli = Path.Combine(RepoRoot.Path, "src", "PCBHelper.Cli", "bin", configuration, framework, "PCBHelper.Cli.dll");
        if (!File.Exists(cli))
        {
            throw new FileNotFoundException("Build PCBHelper.Cli before running contract tests.", cli);
        }

        return cli;
    }

    private static string Tail(string text)
    {
        const int maxLength = 4000;
        return text.Length <= maxLength ? text : text[^maxLength..];
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
