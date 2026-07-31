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
