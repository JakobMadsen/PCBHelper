using System.Text.Json;

namespace PCBHelper.Core.Tests;

public sealed class DesignIntentServiceTests
{
    [Fact]
    public void Missing_Intent_Is_Unavailable_Not_Passed()
    {
        using var fixture = CopyCircuit();
        var result = Service().Analyze(fixture.Path);
        Assert.False(result.Success);
        Assert.Equal("DESIGN_INTENT_UNAVAILABLE", result.Error?.Code);
    }

    [Fact]
    public void Tutorial_Led_With_Resistor_Is_Proven()
    {
        using var fixture = CopyCircuit();
        WriteIntent(fixture.Path, """{"version":1,"signals":[{"net":"LED_A","role":"led-drive"}]}""");
        var result = Service().Analyze(fixture.Path);
        Assert.True(result.Success);
        Assert.Contains(result.Data!.Findings, finding => finding.RuleId == "INTENT-LED-001" && finding.Outcome == DesignIntentOutcome.Proven);
        Assert.True(File.Exists(result.Data.ReportPath));
    }

    [Fact]
    public void Adc_Range_And_Missing_Testpoint_Are_Blocking()
    {
        using var fixture = CopyCircuit();
        WriteIntent(fixture.Path, """
        {"version":1,"signals":[
          {"net":"LED_A","role":"adc-input","minVoltage":0,"maxVoltage":5,"adcMinVoltage":0,"adcMaxVoltage":3.3,"requiredTestpoint":true}
        ]}
        """);
        var result = Service().Analyze(fixture.Path);
        Assert.True(result.Success);
        Assert.False(result.Data!.Passed);
        Assert.Contains(result.Data.Findings, finding => finding.RuleId == "INTENT-ADC-RANGE-001" && finding.Severity == DesignIntentSeverity.Error);
        Assert.Contains(result.Data.Findings, finding => finding.RuleId == "INTENT-TESTPOINT-001" && finding.SuggestedOperationJson is not null);
    }

    [Fact]
    public void Critical_Component_Evidence_And_Rating_Are_Checked()
    {
        using var fixture = CopyCircuit();
        WriteIntent(fixture.Path, """
        {"version":1,"components":[{"reference":"D1","critical":true,"semiconductor":true,
          "manufacturer":"Example","mpn":"LED-1","datasheetUrl":"https://example.test/led.pdf","datasheetRevision":"2026-01","pinMapVerified":false,
          "ratings":[{"kind":"forward-current","maximum":0.02,"unit":"A","source":"datasheet p.2","marginPercent":20,"observedMaximum":0.018}]}]}
        """);
        var result = Service().Analyze(fixture.Path);
        Assert.False(result.Data!.Passed);
        Assert.Contains(result.Data.Findings, finding => finding.RuleId == "INTENT-COMPONENT-EVIDENCE-001" && finding.Severity == DesignIntentSeverity.Error);
        Assert.Contains(result.Data.Findings, finding => finding.RuleId == "INTENT-RATING-001" && finding.Outcome == DesignIntentOutcome.NotProven);
    }

    [Fact]
    public void Set_Intent_Preview_Does_Not_Write_And_Apply_Does()
    {
        using var fixture = CopyCircuit();
        using var json = JsonDocument.Parse("""{"version":1,"signals":[{"net":"LED_A","role":"led-drive"}]}""");
        var service = Service();
        var preview = service.SetIntent(fixture.Path, json.RootElement, true);
        Assert.True(preview.Success);
        Assert.False(File.Exists(Path.Combine(fixture.Path, ".pcbhelper", "design-intent.json")));
        var apply = service.SetIntent(fixture.Path, json.RootElement, false);
        Assert.True(apply.Success);
        Assert.True(File.Exists(apply.Data!.IntentPath));
    }

    [Fact]
    public void Report_Write_Failure_Returns_Stable_Error()
    {
        using var fixture = CopyCircuit();
        WriteIntent(fixture.Path, """{"version":1,"signals":[{"net":"LED_A","role":"led-drive"}]}""");
        File.WriteAllText(Path.Combine(fixture.Path, ".pcbhelper", "intent-runs"), "blocks directory creation");

        var result = Service().Analyze(fixture.Path);

        Assert.False(result.Success);
        Assert.Equal("DESIGN_INTENT_REPORT_WRITE_FAILED", result.Error?.Code);
    }

    [Fact]
    public async Task Invalid_Intent_Is_A_Gate_Finding_Not_Execution_Failure()
    {
        using var fixture = CopyCircuit();
        WriteIntent(fixture.Path, """{"version":1}""");
        using var fakeRoot = new TempDirectory();
        var fakeCli = Path.Combine(fakeRoot.Path, "kicad-cli.exe");
        File.WriteAllText(fakeCli, string.Empty);
        var projects = new ProjectDiscoveryService();
        var locator = new KiCadCliLocator(name => name == "KICAD_CLI" ? fakeCli : null);
        var runner = new FakeCommandRunner();
        var checks = new CheckSummaryService(new CheckRunner(projects, locator, runner));
        var exports = new ExportService(projects, locator, runner);
        var assembly = new AssemblyService(projects, new KiCadDoctorService(locator, runner), exports);
        var intent = new DesignIntentService(projects, new BoardInspectionService(projects));
        var gates = new EngineeringGateService(checks, assembly, designIntent: intent);

        var result = await gates.RunAsync(fixture.Path, new EngineeringGateRequirements("skip", "skip", "skip", "skip", "required"));

        Assert.Equal(EngineeringGateStatus.FindingsPresent, result.Data!.Status);
        Assert.Contains(result.Data.Checks, check => check.Kind == "design-intent" && check.Status == EngineeringGateCheckStatus.FindingsPresent);
    }

    private static DesignIntentService Service()
    {
        var projects = new ProjectDiscoveryService();
        return new DesignIntentService(projects, new BoardInspectionService(projects));
    }

    private static void WriteIntent(string root, string json)
    {
        var directory = Path.Combine(root, ".pcbhelper");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "design-intent.json"), json);
    }

    private static TempDirectory CopyCircuit()
    {
        var temp = new TempDirectory();
        var source = Path.Combine(RepoRoot.Path, "fixtures", "blank-authoring");
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(temp.Path, Path.GetFileName(file)));
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        service.CreateSymbol(temp.Path, "Device:Battery_Cell", "BT1", 30, 50, null, null, false);
        service.CreateSymbol(temp.Path, "Device:R", "R1", 50, 50, "330R", null, false);
        service.CreateSymbol(temp.Path, "Device:LED", "D1", 70, 50, null, null, false);
        service.ConnectPins(temp.Path, "BT1.+", "R1.1", "VCC", false);
        service.ConnectPins(temp.Path, "R1.2", "D1.A", "LED_A", false);
        service.ConnectPins(temp.Path, "D1.K", "BT1.-", "GND", false);
        service.UpdatePcbFromSchematic(temp.Path, false);
        return temp;
    }

    private sealed class FakeCommandRunner : ICommandRunner
    {
        public async Task<CommandExecutionResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken = default)
        {
            for (var index = 0; index < arguments.Count - 1; index++)
                if (arguments[index] == "--output") await File.WriteAllTextAsync(arguments[index + 1], "[]", cancellationToken);
            return new CommandExecutionResult(0, string.Empty, string.Empty);
        }
    }
}
