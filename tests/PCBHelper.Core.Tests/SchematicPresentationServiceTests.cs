using System.Text.Json;

namespace PCBHelper.Core.Tests;

public sealed class SchematicPresentationServiceTests
{
    [Fact]
    public void Arrange_Is_Deterministic_And_Preserves_Connectivity()
    {
        using var fixture = CopyBlankFixture();
        var projects = new ProjectDiscoveryService();
        var authoring = new SchematicAuthoringService(projects);
        CreateLedCircuit(authoring, fixture.Path);
        var before = File.ReadAllText(Path.Combine(fixture.Path, "blank-authoring.kicad_sch"));
        var presentation = new SchematicPresentationService(projects);

        var first = presentation.Arrange(fixture.Path, dryRun: true);
        var second = presentation.Arrange(fixture.Path, dryRun: true);

        Assert.True(first.Success, $"{first.Error?.Code}: {first.Error?.Message}");
        Assert.True(second.Success, $"{second.Error?.Code}: {second.Error?.Message}");
        Assert.True(first.Data!.Connectivity.Equivalent);
        Assert.Equal(first.Data.Connectivity.BeforeSignature, first.Data.Connectivity.AfterSignature);
        Assert.Equal(
            first.Data.FileSnapshots.Single().AfterText,
            second.Data!.FileSnapshots.Single().AfterText);
        Assert.Equal(before, File.ReadAllText(Path.Combine(fixture.Path, "blank-authoring.kicad_sch")));
        Assert.True(first.Data.After.HardConstraintsSatisfied);
    }

    [Fact]
    public void Arrange_Respects_Explicit_Presentation_Locks()
    {
        using var fixture = CopyBlankFixture();
        var projects = new ProjectDiscoveryService();
        var authoring = new SchematicAuthoringService(projects);
        CreateLedCircuit(authoring, fixture.Path);
        var intent = new DesignIntentService(projects, new BoardInspectionService(projects));
        using var json = JsonDocument.Parse("""
        {
          "version": 1,
          "signals": [{ "net": "LED_A", "role": "signal" }],
          "presentation": {
            "blocks": [
              { "id": "source", "label": "Source", "references": ["BT1"], "order": 0 },
              { "id": "load", "label": "Load", "references": ["R1", "D1"], "order": 1 }
            ],
            "lockedReferences": ["BT1"]
          }
        }
        """);
        Assert.True(intent.SetIntent(fixture.Path, json.RootElement, dryRun: false).Success);
        var before = authoring.ListSymbols(fixture.Path).Data!.Symbols.Single(symbol => symbol.Reference == "BT1");

        var result = new SchematicPresentationService(projects).Arrange(fixture.Path, dryRun: false);
        var after = authoring.ListSymbols(fixture.Path).Data!.Symbols.Single(symbol => symbol.Reference == "BT1");

        Assert.True(result.Success, $"{result.Error?.Code}: {result.Error?.Message}");
        Assert.Equal(before.XMillimeters, after.XMillimeters);
        Assert.Equal(before.YMillimeters, after.YMillimeters);
    }

    [Fact]
    public void Arrange_Fails_Closed_For_Unsupported_Top_Level_Graphics()
    {
        using var fixture = CopyFixture("kicad-getting-started-led");
        var result = new SchematicPresentationService(new ProjectDiscoveryService())
            .Arrange(fixture.Path, dryRun: true);

        Assert.False(result.Success);
        Assert.Equal("SCHEMATIC_PRESENTATION_UNSUPPORTED", result.Error?.Code);
    }

    [Fact]
    public void Arrange_Does_Not_Merge_Interlock_Nets_Through_Foreign_Pins()
    {
        using var fixture = CopyBlankFixture();
        var projects = new ProjectDiscoveryService();
        var authoring = new SchematicAuthoringService(projects);
        CreateInterlockCircuit(authoring, fixture.Path);

        var result = new SchematicPresentationService(projects).Arrange(fixture.Path, dryRun: true);

        Assert.True(result.Success, $"{result.Error?.Code}: {result.Error?.Message}");
        Assert.True(result.Data!.Connectivity.Equivalent);
        Assert.Equal(result.Data.Connectivity.BeforeSignature, result.Data.Connectivity.AfterSignature);
    }

    [Fact]
    public void DesignPlan_Preview_Exposes_Readability_And_Connectivity_Evidence()
    {
        using var fixture = CopyBlankFixture();
        var authoring = new SchematicAuthoringService(new ProjectDiscoveryService());
        CreateLedCircuit(authoring, fixture.Path);
        var runtime = PCBHelperRuntime.ForCli();
        var plan = """
        {
          "version": 1,
          "goal": "Arrange the schematic",
          "operations": [{ "id": "arrange", "type": "arrange-schematic" }],
          "engineeringGate": {
            "erc": "skip",
            "drc": "skip",
            "manufacturingValidation": "skip"
          }
        }
        """;

        var preview = runtime.Plans.Preview(fixture.Path, plan);

        Assert.True(preview.Success, $"{preview.Error?.Code}: {preview.Error?.Message}");
        var operation = Assert.Single(preview.Data!.Operations);
        Assert.Contains(operation.Evidence!, evidence => evidence.Kind == "schematic-readability-before");
        Assert.Contains(operation.Evidence!, evidence => evidence.Kind == "schematic-readability-after");
        Assert.Contains(operation.Evidence!, evidence => evidence.Kind == "schematic-connectivity-certificate");
        Assert.Contains(preview.Data.ChangedFiles, file =>
            file.RelativePath.EndsWith(".kicad_sch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DesignPlan_Applies_And_Restores_Arrangement_As_One_Transaction()
    {
        using var fixture = CopyBlankFixture();
        var authoring = new SchematicAuthoringService(new ProjectDiscoveryService());
        CreateLedCircuit(authoring, fixture.Path);
        var schematicPath = Path.Combine(fixture.Path, "blank-authoring.kicad_sch");
        var original = File.ReadAllText(schematicPath);
        var runtime = PCBHelperRuntime.ForCli();
        var plan = """
        {
          "version": 1,
          "goal": "Arrange the schematic",
          "operations": [{ "id": "arrange", "type": "arrange-schematic" }],
          "engineeringGate": {
            "erc": "skip",
            "drc": "skip",
            "manufacturingValidation": "skip"
          }
        }
        """;
        var preview = runtime.Plans.Preview(fixture.Path, plan);
        Assert.True(preview.Success, preview.Error?.Message);

        var applied = await runtime.Plans.ApplyAsync(
            fixture.Path,
            plan,
            preview.Data!.PlanHash,
            preview.Data.RequiredDecisions.Select(decision => decision.DecisionId).ToArray());

        Assert.True(applied.Success, applied.Error?.Message);
        Assert.NotEqual(original, File.ReadAllText(schematicPath));
        var restored = await runtime.Transactions.RestoreAsync(
            fixture.Path,
            applied.Data!.Transaction.Transaction.TransactionId);
        Assert.True(restored.Success, restored.Error?.Message);
        Assert.Equal(original, File.ReadAllText(schematicPath));
    }

    private static void CreateLedCircuit(SchematicAuthoringService authoring, string projectPath)
    {
        Assert.True(authoring.CreateSymbol(projectPath, "Device:Battery_Cell", "BT1", 30, 50, null, null, dryRun: false).Success);
        Assert.True(authoring.CreateSymbol(projectPath, "Device:R", "R1", 55, 50, "330R", null, dryRun: false).Success);
        Assert.True(authoring.CreateSymbol(projectPath, "Device:LED", "D1", 80, 50, null, null, dryRun: false).Success);
        Assert.True(authoring.ConnectPins(projectPath, "BT1.+", "R1.1", "VCC", dryRun: false).Success);
        Assert.True(authoring.ConnectPins(projectPath, "R1.2", "D1.A", "LED_A", dryRun: false).Success);
        Assert.True(authoring.ConnectPins(projectPath, "D1.K", "BT1.-", "GND", dryRun: false).Success);
    }

    private static void CreateInterlockCircuit(SchematicAuthoringService authoring, string projectPath)
    {
        Assert.True(authoring.CreateSymbol(projectPath, "Connector_Generic:Conn_02x07_Odd_Even", "J1", 50, 70, "CORE-14", null, dryRun: false).Success);
        Assert.True(authoring.CreateSymbol(projectPath, "Connector_Generic:Conn_02x10_Odd_Even", "J2", 110, 70, "EXTENDED-20", null, dryRun: false).Success);
        var connectorNets = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["J1"] = ["CORE_SCOPE_CH1", "GND", "CORE_SCOPE_CH2", "GND", "CORE_SCOPE_CH3", "GND", "CORE_SCOPE_CH4", "GND", "CORE_FLEX_IO1", "CORE_FLEX_IO2", "CORE_FLEX_IO3", "CORE_FLEX_IO4", "CORE_ANALOG_IO", "CORE_VTEST"],
            ["J2"] = ["EXT_SCOPE_CH1", "GND", "EXT_SCOPE_CH2", "GND", "EXT_SCOPE_CH3", "GND", "EXT_SCOPE_CH4", "GND", "EXT_FLEX_IO1", "EXT_FLEX_IO2", "EXT_FLEX_IO3", "EXT_FLEX_IO4", "EXT_ANALOG_IO", "EXT_VTEST", "EXT_RESET_N", "EXT_DUT_ID", "EXT_DEBUG_DATA", "EXT_DEBUG_CLK", "EXT_ANALOG_IO2", "GND"]
        };
        foreach (var (reference, nets) in connectorNets)
        {
            var symbol = authoring.ListSymbols(projectPath).Data!.Symbols.Single(item => item.Reference == reference);
            foreach (var pin in symbol.Pins)
                Assert.True(authoring.AddNetLabel(projectPath, nets[int.Parse(pin.Pin) - 1], pin.XMillimeters, pin.YMillimeters, dryRun: false).Success);
        }

        Assert.True(authoring.CreateSymbol(projectPath, "Switch:SW_SPDT", "SW1", 45, 120, "PORT SELECT OFF/CORE/EXT", null, dryRun: false).Success);
        Assert.True(authoring.CreateSymbol(projectPath, "74xx:74LS08", "U1", 75, 115, "SN74HCS08", null, 1, dryRun: false).Success);
        Assert.True(authoring.CreateSymbol(projectPath, "74xx:74LS08", "U1", 75, 135, "SN74HCS08", null, 2, dryRun: false).Success);
        Assert.True(authoring.CreateSymbol(projectPath, "74xx:74LS08", "U1", 75, 150, "SN74HCS08", null, 5, dryRun: false).Success);
        Assert.True(authoring.CreateSymbol(projectPath, "PCBHelper:TPS2553-1", "U2", 110, 115, null, null, dryRun: false).Success);
        Assert.True(authoring.CreateSymbol(projectPath, "PCBHelper:TPS2553-1", "U3", 110, 140, null, null, dryRun: false).Success);
        foreach (var (reference, x, y, value) in new[]
                 {
                     ("R1", 125d, 120d, "66.5k"), ("R2", 125d, 145d, "66.5k"),
                     ("R3", 95d, 125d, "100k"), ("R4", 95d, 150d, "100k"),
                     ("R5", 55d, 110d, "100k"), ("R6", 55d, 135d, "100k"),
                     ("R7", 70d, 125d, "100k"), ("R8", 125d, 132.5d, "10k")
                 })
            Assert.True(authoring.CreateSymbol(projectPath, "Device:R", reference, x, y, value, null, dryRun: false).Success);
        foreach (var (reference, x, y) in new[] { ("C1", 85d, 150d), ("C2", 100d, 107.5d), ("C3", 100d, 157.5d) })
            Assert.True(authoring.CreateSymbol(projectPath, "Device:C", reference, x, y, "100n", null, dryRun: false).Success);

        foreach (var (from, to, net) in new[]
                 {
                     ("SW1.1", "U1.1", "CORE_SELECTED"), ("SW1.3", "U1.4", "EXT_SELECTED"),
                     ("U1.2", "U1.5", "VTEST_ARM"), ("U1.3", "U2.3", "CORE_POWER_EN"),
                     ("U1.6", "U3.3", "EXT_POWER_EN"), ("U2.1", "U3.1", "VTEST_SELECTED"),
                     ("J1.14", "U2.6", "CORE_VTEST"), ("J2.14", "U3.6", "EXT_VTEST"),
                     ("U2.4", "U3.4", "FAULT_N"), ("SW1.2", "U1.14", "LOGIC_3V3"),
                     ("U1.14", "C1.1", "LOGIC_3V3"), ("U1.14", "R8.1", "LOGIC_3V3"),
                     ("R8.2", "U2.4", "FAULT_N"), ("U2.5", "R1.1", "CORE_ILIM"),
                     ("U3.5", "R2.1", "EXT_ILIM"), ("U2.3", "R3.1", "CORE_POWER_EN"),
                     ("U3.3", "R4.1", "EXT_POWER_EN"), ("U1.1", "R5.1", "CORE_SELECTED"),
                     ("U1.4", "R6.1", "EXT_SELECTED"), ("U1.2", "R7.1", "VTEST_ARM"),
                     ("U2.1", "C2.1", "VTEST_SELECTED"), ("U3.1", "C3.1", "VTEST_SELECTED")
                 })
            Assert.True(authoring.ConnectPins(projectPath, from, to, net, dryRun: false).Success);
        foreach (var pin in new[] { "U1.7", "U2.2", "U3.2", "R1.2", "R2.2", "R3.2", "R4.2", "R5.2", "R6.2", "R7.2", "C1.2", "C2.2", "C3.2" })
            Assert.True(authoring.ConnectPins(projectPath, "J1.2", pin, "GND", dryRun: false).Success);
    }

    private static TempDirectory CopyBlankFixture() => CopyFixture("blank-authoring");

    private static TempDirectory CopyFixture(string name)
    {
        var temp = new TempDirectory();
        var source = Path.Combine(RepoRoot.Path, "fixtures", name);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(temp.Path, Path.GetFileName(file)));
        return temp;
    }
}
