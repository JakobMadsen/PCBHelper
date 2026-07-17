using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class PcbWayReleaseServiceTests
{
    [Fact]
    public void Fabrication_Set_Requires_Standard_PcbWay_Layers_And_Drill()
    {
        var complete = new[]
        {
            "board-F_Cu.gtl",
            "board-B_Cu.gbl",
            "board-F_Mask.gts",
            "board-B_Mask.gbs",
            "board-F_Silkscreen.gto",
            "board-Edge_Cuts.gm1",
            "board.drl"
        };

        Assert.Empty(MissingRequiredFabricationFiles(complete));
    }

    [Fact]
    public void Fabrication_Set_Reports_Missing_Copper_Mask_And_Silkscreen()
    {
        var incomplete = new[] { "board-Edge_Cuts.gm1", "board.drl", "board-job.gbrjob" };

        var missing = MissingRequiredFabricationFiles(incomplete);

        Assert.Contains("top copper (.gtl)", missing);
        Assert.Contains("bottom copper (.gbl)", missing);
        Assert.Contains("top solder mask (.gts)", missing);
        Assert.Contains("bottom solder mask (.gbs)", missing);
        Assert.Contains("top silkscreen (.gto)", missing);
    }

    [Theory]
    [InlineData("board-F_Cu.gtl", true)]
    [InlineData("board-B_Cu.gbl", true)]
    [InlineData("board-F_Mask.gts", true)]
    [InlineData("board-B_Mask.gbs", true)]
    [InlineData("board-F_Silkscreen.gto", true)]
    [InlineData("board-B_Silkscreen.gbo", true)]
    [InlineData("board-Edge_Cuts.gm1", true)]
    [InlineData("board.drl", true)]
    [InlineData("board-job.gbrjob", true)]
    [InlineData("board-F_Fab.gbr", false)]
    [InlineData("board-F_Courtyard.gbr", false)]
    [InlineData("board-F_Paste.gtp", false)]
    [InlineData("board-F_Adhesive.gta", false)]
    public void PcbWay_Zip_Includes_Only_Fabrication_Layers(string fileName, bool expected)
    {
        var method = typeof(PcbWayReleaseService).GetMethod(
            "IsPcbWayFabricationFile",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        Assert.Equal(expected, method.Invoke(null, new object[] { fileName }));
    }

    private static IReadOnlyList<string> MissingRequiredFabricationFiles(IEnumerable<string> paths)
    {
        var method = typeof(PcbWayReleaseService).GetMethod(
            "MissingRequiredFabricationFiles",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<IReadOnlyList<string>>(method.Invoke(null, new object[] { paths }));
    }
}
