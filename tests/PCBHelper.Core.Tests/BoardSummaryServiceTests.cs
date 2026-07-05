using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class BoardSummaryServiceTests
{
    [Fact]
    public void GetSummary_Reads_Tutorial_Fixture_Footprints()
    {
        var fixture = Path.Combine(RepoRoot.Path, "fixtures", "kicad-getting-started-led");
        var service = new BoardSummaryService(new ProjectDiscoveryService());

        var result = service.GetSummary(fixture);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var references = result.Data.Footprints.Select(static footprint => footprint.Reference).ToHashSet();
        Assert.Contains("BT1", references);
        Assert.Contains("R1", references);
        Assert.Contains("D1", references);
        Assert.Contains(result.Data.Footprints, static footprint => footprint.Reference == "BT1" && footprint.Side == "back");
    }
}
