namespace PCBHelper.Core.Tests;

public sealed class SchematicPresentationCompatibilityTests
{
    [Theory]
    [InlineData(0, 54.61, 50.8)]
    [InlineData(90, 50.8, 46.99)]
    [InlineData(180, 46.99, 50.8)]
    [InlineData(270, 50.8, 54.61)]
    public void Potentiometer_Wiper_Uses_Rotation_Aware_Pin_Position(
        int rotationDegrees,
        double expectedX,
        double expectedY)
    {
        using var fixture = CopyBlankFixture();
        var authoring = new SchematicAuthoringService(new ProjectDiscoveryService());
        var created = authoring.CreateSymbol(
            fixture.Path,
            "Device:R_Potentiometer",
            "RV1",
            50.8,
            50.8,
            "100k",
            null,
            dryRun: false);
        Assert.True(created.Success, created.Error?.Message);
        Assert.True(authoring.CreateSymbol(
            fixture.Path,
            "Device:R",
            "R1",
            100,
            50.8,
            "1k",
            null,
            dryRun: false).Success);
        SetSymbolRotation(fixture.Path, "RV1", rotationDegrees);

        var connected = authoring.ConnectPins(fixture.Path, "RV1.2", "R1.1", "WIPER", dryRun: true);

        Assert.True(connected.Success, connected.Error?.Message);
        var after = connected.Data!.FileSnapshots.Single().AfterText;
        Assert.Contains($"(xy {Format(expectedX)} {Format(expectedY)})", after, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyzer_Accepts_Text_Box_While_Arranger_Fails_Closed()
    {
        using var fixture = CopyBlankFixture();
        var authoring = new SchematicAuthoringService(new ProjectDiscoveryService());
        Assert.True(authoring.CreateSymbol(
            fixture.Path, "Device:R", "R1", 20.32, 20.32, "1k", null, dryRun: false).Success);
        var schematicPath = Path.Combine(fixture.Path, "blank-authoring.kicad_sch");
        var original = File.ReadAllText(schematicPath);
        var insertion = """

  (text_box "POWER"
    (exclude_from_sim no) (at 10.16 10.16 0) (size 30.48 20.32)
    (stroke (width 0.3) (type default) (color 0 0 0 1))
    (fill (type none))
    (effects (font (size 1.27 1.27) (bold yes)) (justify left top))
    (uuid "6667f9e3-01b2-4a66-bea8-d51e77ad79c1")
  )
""";
        var rootEnd = original.LastIndexOf(')');
        File.WriteAllText(schematicPath, original.Insert(rootEnd, insertion));
        var beforeAnalysis = File.ReadAllText(schematicPath);
        var service = new SchematicPresentationService(new ProjectDiscoveryService());

        var analyzed = service.Analyze(fixture.Path);

        Assert.True(analyzed.Success, analyzed.Error?.Message);
        Assert.Equal("readability-v2", analyzed.Data!.ScoreVersion);
        Assert.Equal(1, analyzed.Data.TextBoxCount);
        Assert.Equal(beforeAnalysis, File.ReadAllText(schematicPath));

        var arranged = service.Arrange(fixture.Path, dryRun: true);

        Assert.False(arranged.Success);
        Assert.Equal("SCHEMATIC_TEXT_BOX_RELAYOUT_UNSUPPORTED", arranged.Error?.Code);
        Assert.Equal(beforeAnalysis, File.ReadAllText(schematicPath));

        var preview = PCBHelperRuntime.ForCli().Plans.Preview(fixture.Path, """
            {
              "version": 1,
              "goal": "Verify closed text-box relayout",
              "operations": [{ "id": "arrange", "type": "arrange-schematic" }]
            }
            """);
        Assert.False(preview.Success);
        Assert.Equal("SCHEMATIC_TEXT_BOX_RELAYOUT_UNSUPPORTED", preview.Error?.Code);
        Assert.Equal(beforeAnalysis, File.ReadAllText(schematicPath));

        var deleted = authoring.DeleteSchematicTextBoxByUuid(
                fixture.Path,
                "6667f9e3-01b2-4a66-bea8-d51e77ad79c1",
                dryRun: false);
        Assert.True(deleted.Success, deleted.Error?.Message);
        Assert.DoesNotContain("6667f9e3-01b2-4a66-bea8-d51e77ad79c1", File.ReadAllText(schematicPath), StringComparison.Ordinal);

        var arrangedAfterExplicitDeletion = service.Arrange(fixture.Path, dryRun: true);
        Assert.True(arrangedAfterExplicitDeletion.Success, arrangedAfterExplicitDeletion.Error?.Message);
        Assert.True(arrangedAfterExplicitDeletion.Data!.Connectivity.Equivalent);
    }

    [Fact]
    public void Analyzer_Measures_Box_Overlap_Boundary_Crossings_And_Explicit_Mapping()
    {
        using var fixture = CopyBlankFixture();
        var authoring = new SchematicAuthoringService(new ProjectDiscoveryService());
        Assert.True(authoring.CreateSymbol(
            fixture.Path, "Device:R", "R1", 20.32, 20.32, "1k", null, dryRun: false).Success);
        var schematicPath = Path.Combine(fixture.Path, "blank-authoring.kicad_sch");
        var text = File.ReadAllText(schematicPath);
        var additions = """

  (wire (pts (xy 0 15.24) (xy 50.8 15.24)) (stroke (width 0) (type default)) (uuid "10000000-0000-0000-0000-000000000001"))
  (label "BOX_NET" (at 0 15.24 0) (effects (font (size 1.27 1.27))) (uuid "10000000-0000-0000-0000-000000000002"))
  (text_box "FIRST" (exclude_from_sim no) (at 10.16 10.16 0) (size 20.32 20.32) (stroke (width 0.3) (type default)) (fill (type none)) (effects (font (size 1.27 1.27))) (uuid "10000000-0000-0000-0000-000000000003"))
  (text_box "SECOND" (exclude_from_sim no) (at 25.4 10.16 0) (size 20.32 20.32) (stroke (width 0.3) (type default)) (fill (type none)) (effects (font (size 1.27 1.27))) (uuid "10000000-0000-0000-0000-000000000004"))
""";
        File.WriteAllText(schematicPath, text.Insert(text.LastIndexOf(')'), additions));
        using var intentJson = System.Text.Json.JsonDocument.Parse("""
        {
          "version": 1,
          "signals": [{ "net": "BOX_NET", "role": "presentation-test" }],
          "presentation": {
            "blocks": [{
              "id": "first",
              "label": "First",
              "references": ["R1"],
              "order": 0,
              "textBoxUuid": "10000000-0000-0000-0000-000000000003"
            }]
          }
        }
        """);
        var intent = new DesignIntentService(new ProjectDiscoveryService(), new BoardInspectionService(new ProjectDiscoveryService()));
        Assert.True(intent.SetIntent(fixture.Path, intentJson.RootElement, dryRun: false).Success);

        var analyzed = new SchematicPresentationService(new ProjectDiscoveryService()).Analyze(fixture.Path);

        Assert.True(analyzed.Success, analyzed.Error?.Message);
        Assert.Equal(2, analyzed.Data!.TextBoxCount);
        Assert.Equal(1, analyzed.Data.TextBoxOverlaps);
        Assert.Equal(4, analyzed.Data.WireTextBoxBoundaryCrossings);
    }

    private static TempDirectory CopyBlankFixture()
    {
        var temp = new TempDirectory();
        var source = Path.Combine(RepoRoot.Path, "fixtures", "blank-authoring");
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(temp.Path, Path.GetFileName(file)));
        return temp;
    }

    private static void SetSymbolRotation(string projectPath, string reference, int rotationDegrees)
    {
        var path = Path.Combine(projectPath, "blank-authoring.kicad_sch");
        var text = File.ReadAllText(path);
        var referenceAt = text.IndexOf($"(property \"Reference\" \"{reference}\"", StringComparison.Ordinal);
        Assert.True(referenceAt >= 0);
        var symbolAt = text.LastIndexOf("  (symbol", referenceAt, StringComparison.Ordinal);
        var placementAt = text.IndexOf("(at 50.8 50.8 0)", symbolAt, StringComparison.Ordinal);
        Assert.True(placementAt >= 0 && placementAt < referenceAt);
        text = text.Remove(placementAt, "(at 50.8 50.8 0)".Length)
            .Insert(placementAt, $"(at 50.8 50.8 {rotationDegrees})");
        File.WriteAllText(path, text);
    }

    private static string Format(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
