using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class GeometryServiceTests
{
    [Fact]
    public void MeasureDistance_Returns_Distance_Between_R1_And_D1()
    {
        using var fixture = CopyTutorialFixture();
        var service = new GeometryService(new ProjectDiscoveryService());

        var result = service.MeasureDistance(fixture.Path, "R1", "D1");

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(23, result.Data.DxMillimeters, precision: 3);
        Assert.Equal(0, result.Data.DyMillimeters, precision: 3);
        Assert.Equal(23, result.Data.DistanceMillimeters, precision: 3);
    }

    [Fact]
    public void MeasureDistance_Returns_Stable_Error_For_Missing_Reference()
    {
        using var fixture = CopyTutorialFixture();
        var service = new GeometryService(new ProjectDiscoveryService());

        var result = service.MeasureDistance(fixture.Path, "R1", "NOPE");

        Assert.False(result.Success);
        Assert.Equal("FOOTPRINT_NOT_FOUND", result.Error?.Code);
    }

    [Fact]
    public void MoveComponent_DryRun_Does_Not_Change_Board_File()
    {
        using var fixture = CopyTutorialFixture();
        var boardFile = Path.Combine(fixture.Path, "kicad-getting-started-led.kicad_pcb");
        var beforeText = File.ReadAllText(boardFile);
        var service = new GeometryService(new ProjectDiscoveryService());

        var result = service.MoveComponent(fixture.Path, "D1", 75, 35, dryRun: true);

        Assert.True(result.Success);
        Assert.Equal(beforeText, File.ReadAllText(boardFile));
        Assert.Equal(68, result.Data?.Before.XMillimeters);
        Assert.Equal(75, result.Data?.After.XMillimeters);
    }

    [Fact]
    public void MoveComponent_Updates_Only_Target_Top_Level_Position()
    {
        using var fixture = CopyTutorialFixture();
        var service = new GeometryService(new ProjectDiscoveryService());

        var result = service.MoveComponent(fixture.Path, "D1", 75, 35, dryRun: false);
        var summary = new BoardSummaryService(new ProjectDiscoveryService()).GetSummary(fixture.Path);

        Assert.True(result.Success);
        var moved = summary.Data?.Footprints.Single(footprint => footprint.Reference == "D1");
        var resistor = summary.Data?.Footprints.Single(footprint => footprint.Reference == "R1");
        Assert.Equal(75, moved?.XMillimeters);
        Assert.Equal(35, moved?.YMillimeters);
        Assert.Equal(45, resistor?.XMillimeters);
        Assert.Equal(35, resistor?.YMillimeters);
    }

    [Fact]
    public void SetComponentSpacing_Moves_Component_To_Target_X_Distance()
    {
        using var fixture = CopyTutorialFixture();
        var service = new GeometryService(new ProjectDiscoveryService());

        var result = service.SetComponentSpacing(fixture.Path, "R1", "D1", 25, "x", dryRun: false);
        var measurement = service.MeasureDistance(fixture.Path, "R1", "D1");

        Assert.True(result.Success);
        Assert.True(measurement.Success);
        Assert.NotNull(measurement.Data);
        Assert.Equal(25, measurement.Data.DxMillimeters, precision: 3);
        Assert.Equal(25, measurement.Data.DistanceMillimeters, precision: 3);
    }

    private static TempDirectory CopyTutorialFixture()
    {
        var temp = new TempDirectory();
        var source = Path.Combine(RepoRoot.Path, "fixtures", "kicad-getting-started-led");
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(temp.Path, Path.GetFileName(file)));
        }

        return temp;
    }
}
