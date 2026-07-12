using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class DesignPlanServiceTests
{
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

        Assert.Equal(22, DesignPlanOperationCatalog.All.Count);
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
