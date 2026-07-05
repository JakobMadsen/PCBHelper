using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class ProjectDiscoveryServiceTests
{
    [Fact]
    public void GetSummary_Returns_KiCad_Files_In_Project_Root()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "demo.kicad_pro"), "{}");
        File.WriteAllText(Path.Combine(temp.Path, "demo.kicad_sch"), "(kicad_sch)");
        File.WriteAllText(Path.Combine(temp.Path, "demo.kicad_pcb"), "(kicad_pcb)");

        var result = new ProjectDiscoveryService().GetSummary(temp.Path);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("demo", result.Data.ProjectName);
        Assert.Empty(result.Data.MissingFiles);
        Assert.EndsWith("demo.kicad_pro", result.Data.ProjectFile);
        Assert.EndsWith("demo.kicad_sch", result.Data.SchematicFile);
        Assert.EndsWith("demo.kicad_pcb", result.Data.BoardFile);
    }

    [Fact]
    public void GetSummary_Returns_Missing_Files_For_Incomplete_Project()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "demo.kicad_pro"), "{}");

        var result = new ProjectDiscoveryService().GetSummary(temp.Path);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Contains(".kicad_sch", result.Data.MissingFiles);
        Assert.Contains(".kicad_pcb", result.Data.MissingFiles);
    }
}
