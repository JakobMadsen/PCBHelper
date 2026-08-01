using System.Text.Json;

namespace PCBHelper.Core.Tests;

public sealed class ConstraintProofServiceTests
{
    [Fact]
    public async Task Evaluate_Measures_HashBound_Layout_Proofs_Without_Changing_Board()
    {
        using var fixture = CreateProject(filledZone: true);
        WriteConstraints(fixture.Path,
            """
            [
              { "id": "front", "type": "allowed-layers", "net": "I", "allowedLayers": ["F.Cu"] },
              { "id": "distance", "type": "max-feature-distance", "from": "U1.1", "to": "C1", "maximumMm": 3 },
              { "id": "area", "type": "min-copper-area", "net": "GND", "layer": "F.Cu", "minimumSquareMm": 100 },
              { "id": "islands", "type": "max-zone-islands", "net": "GND", "layer": "F.Cu", "maximumCount": 1 },
              { "id": "length", "type": "max-trace-length", "net": "I", "maximumMm": 10 },
              { "id": "matched", "type": "matched-trace-length", "nets": ["I", "Q"], "maximumDifferenceMm": 0 },
              { "id": "vias", "type": "max-vias", "net": "I", "maximumCount": 1 },
              { "id": "region", "type": "footprint-region", "feature": "U1", "minimumXmm": 9, "maximumXmm": 11, "minimumYmm": 9, "maximumYmm": 11 }
            ]
            """);
        var board = Directory.GetFiles(fixture.Path, "*.kicad_pcb").Single();
        var before = File.ReadAllBytes(board);

        var result = await new ConstraintProofService(new ProjectDiscoveryService()).EvaluateAsync(fixture.Path);

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(result.Data!.Passed);
        Assert.All(result.Data.Results, item => Assert.Equal(ConstraintOutcome.Passed, item.Outcome));
        Assert.Equal(before, File.ReadAllBytes(board));
        Assert.True(File.Exists(result.Data.ReportPath));
        using var report = JsonDocument.Parse(File.ReadAllText(result.Data.ReportPath));
        Assert.Equal(result.Data.InputHash, report.RootElement.GetProperty("inputHash").GetString());
    }

    [Fact]
    public async Task Missing_Nets_And_Unfilled_Zones_Are_Unavailable_Never_Passing()
    {
        using var fixture = CreateProject(filledZone: false);
        WriteConstraints(fixture.Path,
            """
            [
              { "id": "layers", "type": "allowed-layers", "net": "MISSING", "allowedLayers": ["F.Cu"] },
              { "id": "length", "type": "max-trace-length", "net": "MISSING", "maximumMm": 10 },
              { "id": "unrouted", "type": "max-trace-length", "net": "GND", "maximumMm": 10 },
              { "id": "matched", "type": "matched-trace-length", "nets": ["I", "MISSING"], "maximumDifferenceMm": 1 },
              { "id": "vias", "type": "max-vias", "net": "MISSING", "maximumCount": 0 },
              { "id": "area", "type": "min-copper-area", "net": "GND", "layer": "F.Cu", "minimumSquareMm": 1 },
              { "id": "islands", "type": "max-zone-islands", "net": "GND", "layer": "F.Cu", "maximumCount": 0 }
            ]
            """);

        var result = await new ConstraintProofService(new ProjectDiscoveryService()).EvaluateAsync(fixture.Path);

        Assert.True(result.Success, result.Error?.Message);
        Assert.False(result.Data!.Passed);
        Assert.All(result.Data.Results, item => Assert.Equal(ConstraintOutcome.Unavailable, item.Outcome));
    }

    [Fact]
    public void Validate_Rejects_Duplicate_Ids_And_Constraint_Path_Escape()
    {
        using var fixture = CreateProject(filledZone: false);
        WriteConstraints(fixture.Path,
            """
            [
              { "id": "same", "type": "max-vias", "maximumCount": 1 },
              { "id": "same", "type": "max-vias", "maximumCount": 2 }
            ]
            """);
        var service = new ConstraintProofService(new ProjectDiscoveryService());

        var duplicate = service.Validate(fixture.Path);
        var escaped = service.Validate(fixture.Path, Path.Combine(fixture.Path, "..", "outside.json"));

        Assert.False(duplicate.Success);
        Assert.Contains("unique", duplicate.Data!.Errors.Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("PROJECT_SCOPE_VIOLATION", escaped.Error?.Code);
    }

    private static TempDirectory CreateProject(bool filledZone)
    {
        var fixture = new TempDirectory();
        File.WriteAllText(Path.Combine(fixture.Path, "proof.kicad_pro"), "{}");
        File.WriteAllText(Path.Combine(fixture.Path, "proof.kicad_pcb"), $$"""
            (kicad_pcb
              (version 20250114)
              (net 1 "GND")
              (net 2 "I")
              (net 3 "Q")
              (footprint "Test:Point" (layer "F.Cu") (at 10 10 90) (property "Reference" "U1")
                (pad "1" smd rect (at -1 0) (size 1 1) (layers "F.Cu") (net 2 "I")))
              (footprint "Test:Point" (layer "F.Cu") (at 12 10) (property "Reference" "C1")
                (pad "1" smd rect (at 0 0) (size 1 1) (layers "F.Cu") (net 1 "GND")))
              (segment (start 10 11) (end 20 11) (width 0.25) (layer "F.Cu") (net 2) (uuid "i-segment"))
              (segment (start 10 12) (end 20 12) (width 0.25) (layer "F.Cu") (net 3) (uuid "q-segment"))
              (via (at 15 11) (size 0.8) (drill 0.4) (layers "F.Cu" "B.Cu") (net 2) (uuid "i-via"))
              (zone (net 1) (net_name "GND") (layer "F.Cu")
                {{(filledZone ? "(filled_polygon (pts (xy 0 0) (xy 10 0) (xy 10 10) (xy 0 10)))" : string.Empty)}})
            )
            """);
        return fixture;
    }

    private static void WriteConstraints(string root, string constraints)
    {
        var directory = Path.Combine(root, ".pcbhelper");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "constraints-v1.json"), $$"""{ "version": 1, "constraints": {{constraints}} }""");
    }
}
