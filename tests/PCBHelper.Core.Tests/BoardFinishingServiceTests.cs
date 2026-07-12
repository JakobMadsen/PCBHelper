using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class BoardFinishingServiceTests
{
    [Fact]
    public void AddZone_DryRun_Does_Not_Write_And_Produces_Zone()
    {
        using var fixture=CopyTutorial();var board=Directory.GetFiles(fixture.Path,"*.kicad_pcb").Single();var before=File.ReadAllText(board);
        var result=new BoardFinishingService(new ProjectDiscoveryService()).AddCopperZone(fixture.Path,"GND","B.Cu","40,30;75,30;75,60;40,60",0.2,0.25,true);
        Assert.True(result.Success,result.Error?.Message);Assert.Contains("(zone",result.Data!.ProposedText);Assert.Equal(before,File.ReadAllText(board));
    }

    [Fact]
    public void AddTestpoint_And_MountingHole_Are_Parsed_As_Footprints()
    {
        using var fixture=CopyTutorial();var service=new BoardFinishingService(new ProjectDiscoveryService());
        Assert.True(service.AddTestPoint(fixture.Path,"TP1","GND",50,45,1.8,false).Success);
        Assert.True(service.AddMountingHole(fixture.Path,"H1",42,32,3.2,6,false).Success);
        var summary=new BoardSummaryService(new ProjectDiscoveryService()).GetSummary(fixture.Path);
        Assert.Contains(summary.Data!.Footprints,f=>f.Reference=="TP1");Assert.Contains(summary.Data.Footprints,f=>f.Reference=="H1");
    }

    [Fact]
    public void Zone_Update_And_Reference_Hide_Preserve_KiCad_Structure()
    {
        using var fixture=CopyTutorial();var service=new BoardFinishingService(new ProjectDiscoveryService());
        var added=service.AddCopperZone(fixture.Path,"GND","B.Cu","40,30;75,30;75,60;40,60",0.2,0.25,false);
        Assert.True(added.Success,added.Error?.Message);
        var updated=service.UpdateCopperZone(fixture.Path,added.Data!.ItemId,null,null,"41,31;74,31;74,59;41,59",false);
        Assert.True(updated.Success,updated.Error?.Message);
        Assert.True(service.HideReferenceText(fixture.Path,"R1",false).Success);
        var text=File.ReadAllText(Directory.GetFiles(fixture.Path,"*.kicad_pcb").Single());
        Assert.Contains("(xy 41 31)",text);Assert.Contains("(layer \"F.SilkS\") (hide yes)",text);
    }

    [Fact]
    public void ReleaseRequirements_Block_Missing_Required_Testpoints()
    {
        using var fixture=CopyTutorial();File.WriteAllText(Path.Combine(fixture.Path,"requirements.md"),"Testpoints required.");
        var runtime=PCBHelperRuntime.ForCli();var result=runtime.Releases.ValidateRequirements(fixture.Path);
        Assert.True(result.Success);Assert.False(result.Data!.Passed);Assert.Contains(result.Data.Checks,c=>c.Id=="testpoints"&&c.Required&&!c.Implemented);
    }
    private static TempDirectory CopyTutorial(){var t=new TempDirectory();var s=Path.Combine(RepoRoot.Path,"fixtures","kicad-getting-started-led");foreach(var f in Directory.GetFiles(s))File.Copy(f,Path.Combine(t.Path,Path.GetFileName(f)));return t;}
}
