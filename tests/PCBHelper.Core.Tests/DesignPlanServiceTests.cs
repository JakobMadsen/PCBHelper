using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class DesignPlanServiceTests
{
    [Fact]
    public void SetDesignIntent_Is_Prepared_As_ProjectScoped_Transaction_File()
    {
        using var fixture = CopyTutorialFixture();
        var runtime = PCBHelperRuntime.ForCli();
        var plan = """{"version":1,"goal":"Declare intent","operations":[{"id":"intent","type":"set-design-intent","intent":{"version":1,"signals":[{"net":"LED_A","role":"led-drive"}]}}]}""";

        var preview = runtime.Plans.Preview(fixture.Path, plan);

        Assert.True(preview.Success, preview.Error?.Message);
        Assert.Contains(preview.Data!.ChangedFiles, file => file.RelativePath.Replace('\\', '/') == ".pcbhelper/design-intent.json");
    }

    [Fact]
    public void Project_Local_Symbol_Library_Is_Included_In_Design_Plan_Preview()
    {
        using var fixture = CopyTutorialFixture();
        var runtime = PCBHelperRuntime.ForCli();
        var plan = """{"version":1,"goal":"Add protected switch","operations":[{"id":"switch","type":"create-schematic-symbol","symbol":"PCBHelper:TPS2553-1","reference":"U99","xMm":90,"yMm":90}]}""";

        var preview = runtime.Plans.Preview(fixture.Path, plan);

        Assert.True(preview.Success, preview.Error?.Message);
        Assert.Contains(preview.Data!.ChangedFiles, file => file.RelativePath == "PCBHelper.kicad_sym");
        Assert.Contains(preview.Data.ChangedFiles, file => file.RelativePath == "sym-lib-table");
        Assert.False(File.Exists(Path.Combine(fixture.Path, "PCBHelper.kicad_sym")));
        Assert.False(File.Exists(Path.Combine(fixture.Path, "sym-lib-table")));
    }

    [Fact]
    public void MarkSchematicPinNoConnect_Is_Prepared_Transactionally_After_Placement()
    {
        using var fixture = CopyTutorialFixture();
        var runtime = PCBHelperRuntime.ForCli();
        var plan = """{"version":1,"goal":"Place an intentionally unused resistor","operations":[{"id":"place","type":"create-schematic-symbol","symbol":"Device:R","reference":"R99","xMm":90,"yMm":90},{"id":"unused","type":"mark-schematic-pin-no-connect","pin":"R99.1"}]}""";
        var schematicPath = Directory.GetFiles(fixture.Path, "*.kicad_sch").Single();
        var before = File.ReadAllText(schematicPath);

        var preview = runtime.Plans.Preview(fixture.Path, plan);

        Assert.True(preview.Success, preview.Error?.Message);
        Assert.Contains(preview.Data!.ChangedFiles, file => file.RelativePath.EndsWith(".kicad_sch", StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllText(schematicPath));
    }

    [Fact]
    public async Task SetDesignIntent_Applies_And_Restores_Through_Transaction_Engine()
    {
        using var fixture = CopyTutorialFixture();
        var runtime = PCBHelperRuntime.ForCli();
        var plan = """{"version":1,"goal":"Declare intent","operations":[{"id":"intent","type":"set-design-intent","intent":{"version":1,"signals":[{"net":"LED_A","role":"led-drive"}]}}],"engineeringGate":{"erc":"skip","drc":"skip","manufacturingValidation":"skip","simulationAssertions":"skip","designIntent":"optional"}}""";
        var preview = runtime.Plans.Preview(fixture.Path, plan);
        Assert.True(preview.Success, preview.Error?.Message);

        var applied = await runtime.Plans.ApplyAsync(fixture.Path, plan, preview.Data!.PlanHash,
            preview.Data.RequiredDecisions.Select(decision => decision.DecisionId).ToArray());
        Assert.True(applied.Success, applied.Error?.Message);
        var intentPath = Path.Combine(fixture.Path, ".pcbhelper", "design-intent.json");
        Assert.True(File.Exists(intentPath));

        var restored = await runtime.Transactions.RestoreAsync(fixture.Path, applied.Data!.Transaction.Transaction.TransactionId);
        Assert.True(restored.Success, restored.Error?.Message);
        Assert.False(File.Exists(intentPath));
    }

    [Fact]
    public void Validate_Uses_Canonical_Hash_Independent_Of_Property_Order()
    {
        using var fixture = CopyTutorialFixture();
        var runtime = PCBHelperRuntime.ForCli();
        var first = runtime.Plans.Validate(fixture.Path, Plan("300R"));
        var reordered = runtime.Plans.Validate(fixture.Path,
            """{"operations":[{"value":"300R","reference":"R1","type":"set-component-value","id":"value"}],"goal":"Change resistor","version":1,"engineeringGate":{"manufacturingValidation":"skip","drc":"skip","erc":"skip"}}""");

        Assert.True(first.Success);
        Assert.True(reordered.Success);
        Assert.Equal(first.Data!.PlanHash, reordered.Data!.PlanHash);
    }

    [Fact]
    public void Validate_Rejects_Duplicate_Operation_Ids()
    {
        using var fixture = CopyTutorialFixture();
        var runtime = PCBHelperRuntime.ForCli();
        var json = """{"version":1,"goal":"x","operations":[{"id":"same","type":"set-component-value","reference":"R1","value":"300R"},{"id":"same","type":"set-component-value","reference":"R1","value":"330R"}]}""";

        var result = runtime.Plans.Validate(fixture.Path, json);

        Assert.False(result.Success);
        Assert.Equal("PLAN_OPERATION_ID_DUPLICATE", result.Error?.Code);
    }

    [Fact]
    public void Operation_Catalog_Produces_A_Schema_For_Every_Operation()
    {
        var schema = DesignPlanOperationCatalog.CreateJsonSchema();
        using var document = System.Text.Json.JsonDocument.Parse(schema);

        Assert.Equal(39, DesignPlanOperationCatalog.All.Count);
        foreach (var operation in DesignPlanOperationCatalog.All)
            Assert.Contains(operation.Type, schema, StringComparison.Ordinal);
        Assert.Equal(AgentGuidanceService.DesignPlanSchemaUri, document.RootElement.GetProperty("$id").GetString());
    }

    [Theory]
    [InlineData("{\"version\":1,\"goal\":\"x\",\"operations\":[{\"id\":\"x\",\"type\":\"move-component\",\"reference\":\"R1\",\"xMm\":1}]}")]
    [InlineData("{\"version\":1,\"goal\":\"x\",\"operations\":[{\"id\":\"x\",\"type\":\"set-component-value\",\"reference\":\"R1\",\"value\":\"1k\",\"command\":\"bad\"}]}")]
    public void Validate_Rejects_Missing_And_Unknown_Operation_Properties(string plan)
    {
        using var fixture = CopyTutorialFixture();
        var result = PCBHelperRuntime.ForCli().Plans.Validate(fixture.Path, plan);

        Assert.False(result.Success);
        Assert.Equal("PLAN_INVALID", result.Error?.Code);
    }

    [Fact]
    public void Agent_Guide_And_Capabilities_Share_The_Same_Versioned_Contract()
    {
        var service = new AgentGuidanceService();
        var guide = service.GetGuide();
        var capabilities = service.GetCapabilities("workflow");

        Assert.Equal(AgentGuidanceService.GuideVersion, guide.GuideVersion);
        Assert.Equal(guide.Uri, capabilities.AgentGuideUri);
        Assert.Equal(DesignPlanOperationCatalog.All.Count, capabilities.Operations.Count);
        Assert.Contains(capabilities.ApprovedSymbols, item => item.SymbolId == "Amplifier_Operational:OPA1612AxD");
        Assert.Contains(capabilities.ApprovedSymbols, item => item.SymbolId == "Regulator_Linear:LM1117-5.0");
        Assert.Contains(capabilities.ApprovedSymbols, item => item.SymbolId == "Comparator:TLV7011");
        Assert.Contains(capabilities.ApprovedSymbols, item => item.SymbolId == "Device:R_Potentiometer");
        Assert.Contains(capabilities.ApprovedSymbols, item => item.SymbolId == "Connector_Generic:Conn_01x03");
        Assert.Contains(capabilities.ApprovedSymbols, item => item.SymbolId == "Connector_Generic:Conn_02x07_Odd_Even");
        Assert.Contains(capabilities.ApprovedSymbols, item => item.SymbolId == "Connector_Generic:Conn_02x10_Odd_Even");
        Assert.Contains(capabilities.ApprovedSymbols, item => item.SymbolId == "Switch:SW_Push");
        Assert.Contains(capabilities.ApprovedSymbols, item => item.SymbolId == "PCBHelper:TPS2553-1");
        Assert.Equal(11, capabilities.CapabilityVersion);
        Assert.Contains(capabilities.Operations, item => item.Type == "mark-schematic-pin-no-connect");
        var moveReference = Assert.Single(capabilities.Operations, item => item.Type == "move-reference-text");
        Assert.Contains("footprint-local", moveReference.Description, StringComparison.Ordinal);
        Assert.All(AgentPolicyRules.All, rule => Assert.Contains(rule.Id, guide.Markdown, StringComparison.Ordinal));
    }

    [Fact]
    public void Preview_Does_Not_Write_Project()
    {
        using var fixture = CopyTutorialFixture();
        var runtime = PCBHelperRuntime.ForCli();
        var board = Directory.GetFiles(fixture.Path, "*.kicad_pcb").Single();
        var before = File.ReadAllText(board);

        var result = runtime.Plans.Preview(fixture.Path, Plan("300R"));

        Assert.True(result.Success, result.Error?.Message);
        Assert.Single(result.Data!.ChangedFiles);
        Assert.Equal(before, File.ReadAllText(board));
    }

    [Fact]
    public void RotateComponent_Is_Available_As_A_Transactional_DesignPlan_Operation()
    {
        using var fixture = CopyTutorialFixture();
        var runtime = PCBHelperRuntime.ForCli();
        var plan = """
        {
          "version": 1,
          "goal": "Rotate one footprint",
          "operations": [
            {
              "id": "rotate-led",
              "type": "rotate-component",
              "reference": "D1",
              "rotationDegrees": 270
            }
          ],
          "engineeringGate": {
            "erc": "skip",
            "drc": "skip",
            "manufacturingValidation": "skip"
          }
        }
        """;

        var preview = runtime.Plans.Preview(fixture.Path, plan);

        Assert.True(preview.Success, preview.Error?.Message);
        Assert.Contains(preview.Data!.ChangedFiles, file =>
            file.RelativePath.EndsWith(".kicad_pcb", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DeleteSchematicWire_Is_Available_As_A_Transactional_DesignPlan_Operation()
    {
        using var fixture = CopyTutorialFixture();
        var runtime = PCBHelperRuntime.ForCli();
        var authoring = new SchematicAuthoringService(new ProjectDiscoveryService());
        Assert.True(authoring.CreateSymbol(fixture.Path, "Device:R", "R99", 50, 50, "0R", null, 1, dryRun: false).Success);
        Assert.True(authoring.CreateSymbol(fixture.Path, "Device:R", "R100", 70, 50, "0R", null, 1, dryRun: false).Success);
        Assert.True(authoring.ConnectPins(fixture.Path, "R99.2", "R100.1", "JUMPER_TEST", dryRun: false).Success);
        var schematic = authoring.ListSymbols(fixture.Path);
        Assert.True(schematic.Success, schematic.Error?.Message);
        var wire = Assert.Single(schematic.Data!.Wires.Take(1));
        var plan = $$"""
        {
          "version": 1,
          "goal": "Remove one exact schematic wire",
          "operations": [
            {
              "id": "delete-wire",
              "type": "delete-schematic-wire-by-uuid",
              "uuid": "{{wire.Uuid}}"
            }
          ],
          "engineeringGate": {
            "erc": "skip",
            "drc": "skip",
            "manufacturingValidation": "skip"
          }
        }
        """;

        var preview = runtime.Plans.Preview(fixture.Path, plan);

        Assert.True(preview.Success, preview.Error?.Message);
        Assert.Contains(preview.Data!.ChangedFiles, file => file.RelativePath.EndsWith(".kicad_sch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DeleteNetLabel_Is_Available_As_A_Transactional_DesignPlan_Operation()
    {
        using var fixture = CopyTutorialFixture();
        var runtime = PCBHelperRuntime.ForCli();
        var authoring = new SchematicAuthoringService(new ProjectDiscoveryService());
        Assert.True(authoring.AddNetLabel(fixture.Path, "STALE_LABEL", 50, 50, dryRun: false).Success);
        var schematic = authoring.ListSymbols(fixture.Path);
        Assert.True(schematic.Success, schematic.Error?.Message);
        var label = Assert.Single(schematic.Data!.Labels, item => item.Text == "STALE_LABEL");
        var plan = $$"""
        {
          "version": 1,
          "goal": "Remove one exact schematic net label",
          "operations": [
            {
              "id": "delete-label",
              "type": "delete-net-label-by-uuid",
              "uuid": "{{label.Uuid}}"
            }
          ],
          "engineeringGate": {
            "erc": "skip",
            "drc": "skip",
            "manufacturingValidation": "skip"
          }
        }
        """;

        var preview = runtime.Plans.Preview(fixture.Path, plan);

        Assert.True(preview.Success, preview.Error?.Message);
        Assert.Contains(preview.Data!.ChangedFiles, file => file.RelativePath.EndsWith(".kicad_sch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Preview_Does_Not_Confuse_Remains_With_Mains()
    {
        using var fixture = CopyTutorialFixture();
        var plan = """
        {
          "version": 1,
          "goal": "Signal remains within limits",
          "operations": [
            { "id": "value", "type": "set-component-value", "reference": "R1", "value": "300R" }
          ]
        }
        """;

        var preview = PCBHelperRuntime.ForCli().Plans.Preview(fixture.Path, plan);

        Assert.True(preview.Success, preview.Error?.Message);
        Assert.NotEqual(PlanRisk.Blocked, preview.Data!.Risk);
    }

    [Fact]
    public async Task Apply_And_Restore_Use_One_Transaction()
    {
        using var fixture = CopyTutorialFixture();
        var runtime = PCBHelperRuntime.ForCli();
        var plan = Plan("300R");
        var preview = runtime.Plans.Preview(fixture.Path, plan);

        var applied = await runtime.Plans.ApplyAsync(fixture.Path, plan, preview.Data!.PlanHash, preview.Data.RequiredDecisions.Select(static decision => decision.DecisionId).ToArray());

        Assert.True(applied.Success, applied.Error?.Message);
        Assert.Equal(ProjectTransactionStatus.GatePassed, applied.Data!.Transaction.Transaction.Status);
        Assert.Contains(runtime.Components.GetValue(fixture.Path, "R1").Data!.Locations, location => location.Value == "300R");

        var restored = await runtime.Transactions.RestoreAsync(fixture.Path, applied.Data.Transaction.Transaction.TransactionId);
        Assert.True(restored.Success, restored.Error?.Message);
        Assert.Contains(runtime.Components.GetValue(fixture.Path, "R1").Data!.Locations, location => location.Value == "330R");
    }

    [Fact]
    public async Task Restore_Rejects_Changes_Made_After_Transaction()
    {
        using var fixture = CopyTutorialFixture();
        var runtime = PCBHelperRuntime.ForCli();
        var plan = Plan("300R");
        var preview = runtime.Plans.Preview(fixture.Path, plan);
        var applied = await runtime.Plans.ApplyAsync(fixture.Path, plan, preview.Data!.PlanHash, preview.Data.RequiredDecisions.Select(static decision => decision.DecisionId).ToArray());
        var board = Directory.GetFiles(fixture.Path, "*.kicad_pcb").Single();
        File.AppendAllText(board, "\n# external change");

        var restored = await runtime.Transactions.RestoreAsync(fixture.Path, applied.Data!.Transaction.Transaction.TransactionId);

        Assert.False(restored.Success);
        Assert.Equal("TRANSACTION_CONFLICT", restored.Error?.Code);
    }

    private static string Plan(string value) => $$"""
        {
          "version": 1,
          "goal": "Change resistor",
          "operations": [
            { "id": "value", "type": "set-component-value", "reference": "R1", "value": "{{value}}" }
          ],
          "engineeringGate": { "erc": "skip", "drc": "skip", "manufacturingValidation": "skip" }
        }
        """;

    private static TempDirectory CopyTutorialFixture()
    {
        var temp = new TempDirectory();
        var source = Path.Combine(RepoRoot.Path, "fixtures", "kicad-getting-started-led");
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(temp.Path, Path.GetFileName(file)));
        return temp;
    }
}
