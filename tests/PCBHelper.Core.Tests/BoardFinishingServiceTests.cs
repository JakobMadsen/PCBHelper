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
    public void Generated_Testpoints_And_Holes_Use_Resolvable_Library_Links_And_BoardOnly_Attributes()
    {
        using var fixture=CopyTutorial();var service=new BoardFinishingService(new ProjectDiscoveryService());

        var testpoint=service.AddTestPoint(fixture.Path,"TP1","GND",50,45,2,false);
        var hole=service.AddMountingHole(fixture.Path,"H1",42,32,3.2,6,false);
        var board=File.ReadAllText(Directory.GetFiles(fixture.Path,"*.kicad_pcb").Single());

        Assert.True(testpoint.Success,testpoint.Error?.Message);
        Assert.True(hole.Success,hole.Error?.Message);
        Assert.Contains("""(footprint "PCBHelper:TestPoint_THTPad_D2mm_Drill1mm" """,board);
        Assert.Contains("""(footprint "PCBHelper:MountingHole_NPTH_D6mm_Drill3.2mm" """,board);
        Assert.Equal(2,System.Text.RegularExpressions.Regex.Matches(board,@"\(attr board_only exclude_from_pos_files exclude_from_bom\)").Count);
    }

    [Fact]
    public async Task RefillZones_Uses_KiCad_Backend_And_Atomically_Writes_Result()
    {
        using var fixture=CopyTutorial();
        var service=new BoardFinishingService(
            new ProjectDiscoveryService(),
            new FakeZoneRefillBackend(succeed:true),
            new AtomicProjectFileWriter());
        Assert.True(service.AddCopperZone(fixture.Path,"GND","B.Cu","40,30;75,30;75,60;40,60",0.2,0.25,false).Success);

        var result=await service.RefillZonesAsync(fixture.Path);

        Assert.True(result.Success,result.Error?.Message);
        Assert.Equal(1,result.Data!.ZoneCount);
        Assert.NotEqual(result.Data.BeforeHash,result.Data.AfterHash);
        Assert.Contains("(filled_polygon",File.ReadAllText(Directory.GetFiles(fixture.Path,"*.kicad_pcb").Single()));
    }

    [Fact]
    public async Task RefillZones_Backend_Failure_Leaves_Project_Board_Unchanged()
    {
        using var fixture=CopyTutorial();
        var service=new BoardFinishingService(
            new ProjectDiscoveryService(),
            new FakeZoneRefillBackend(succeed:false),
            new AtomicProjectFileWriter());
        Assert.True(service.AddCopperZone(fixture.Path,"GND","B.Cu","40,30;75,30;75,60;40,60",0.2,0.25,false).Success);
        var board=Directory.GetFiles(fixture.Path,"*.kicad_pcb").Single();
        var before=File.ReadAllText(board);

        var result=await service.RefillZonesAsync(fixture.Path);

        Assert.False(result.Success);
        Assert.Equal("KICAD_ZONE_REFILL_FAILED",result.Error!.Code);
        Assert.Equal(before,File.ReadAllText(board));
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
    public void MoveReferenceText_Preserves_Regex_Capture_When_X_Starts_With_Digits()
    {
        using var fixture=CopyTutorial();var service=new BoardFinishingService(new ProjectDiscoveryService());

        var moved=service.MoveReferenceText(fixture.Path,"R1",100,140.5,false);

        Assert.True(moved.Success,moved.Error?.Message);
        var text=File.ReadAllText(Directory.GetFiles(fixture.Path,"*.kicad_pcb").Single());
        Assert.Contains("(at 100 140.5",text,StringComparison.Ordinal);
        Assert.DoesNotContain("$1100",text,StringComparison.Ordinal);
        var summary=new BoardSummaryService(new ProjectDiscoveryService()).GetSummary(fixture.Path);
        Assert.True(summary.Success,summary.Error?.Message);
        Assert.Contains(summary.Data!.Footprints,f=>f.Reference=="R1");
    }

    [Fact]
    public void ModuleKeepout_Allows_Masked_Tracks_And_Blocks_Pours_And_Vias()
    {
        using var fixture=CopyTutorial();var service=new BoardFinishingService(new ProjectDiscoveryService());
        var routing=new RoutingService(new ProjectDiscoveryService());
        var viasBefore=routing.ListVias(fixture.Path).Data!.Vias.Count;
        var footprintsBefore=new BoardSummaryService(new ProjectDiscoveryService()).GetSummary(fixture.Path).Data!.Footprints.Count;

        var result=service.AddModuleKeepout(fixture.Path,"B.Cu","40,30;75,30;75,60;40,60",false);
        var text=File.ReadAllText(Directory.GetFiles(fixture.Path,"*.kicad_pcb").Single());

        Assert.True(result.Success,result.Error?.Message);
        Assert.Contains("(tracks allowed)",text);
        Assert.Contains("(vias not_allowed)",text);
        Assert.Contains("(pads allowed)",text);
        Assert.Contains("(copperpour not_allowed)",text);
        Assert.Contains("(connect_pads",text);
        Assert.Contains("(min_thickness 0.25)",text);
        Assert.Contains("(filled_areas_thickness no)",text);
        Assert.Contains("(placement",text);
        Assert.Contains("(fill",text);
        Assert.Equal(viasBefore,routing.ListVias(fixture.Path).Data!.Vias.Count);
        Assert.Equal(footprintsBefore,new BoardSummaryService(new ProjectDiscoveryService()).GetSummary(fixture.Path).Data!.Footprints.Count);
    }

    [Fact]
    public void MountingHoleKeepout_Blocks_All_Copper_Routing_While_Allowing_The_Hole_Pad()
    {
        using var fixture=CopyTutorial();var service=new BoardFinishingService(new ProjectDiscoveryService());

        var result=service.AddMountingHoleKeepout(fixture.Path,"B.Cu","40,30;48,30;48,38;40,38",false);
        var text=File.ReadAllText(Directory.GetFiles(fixture.Path,"*.kicad_pcb").Single());

        Assert.True(result.Success,result.Error?.Message);
        Assert.Contains("(tracks not_allowed)",text);
        Assert.Contains("(vias not_allowed)",text);
        Assert.Contains("(pads allowed)",text);
        Assert.Contains("(copperpour not_allowed)",text);
        Assert.Contains("(footprints allowed)",text);
    }

    [Fact]
    public void SetBoardOutlineRectangle_Updates_The_Single_EdgeCuts_Rectangle()
    {
        using var fixture=CopyTutorial();
        var boardFile=Directory.GetFiles(fixture.Path,"*.kicad_pcb").Single();
        var boardBefore=File.ReadAllText(boardFile);
        File.WriteAllText(boardFile,boardBefore.Insert(boardBefore.LastIndexOf(')'),"\n(gr_rect (start 40 30) (end 75 60) (stroke (width 0.1) (type default)) (fill no) (layer \"Edge.Cuts\"))\n"));
        var service=new BoardFinishingService(new ProjectDiscoveryService());

        var result=service.SetBoardOutlineRectangle(fixture.Path,10,20,100,80,false);
        var text=File.ReadAllText(boardFile);

        Assert.True(result.Success,result.Error?.Message);
        Assert.Contains("(start 10 20)",text);
        Assert.Contains("(end 100 80)",text);
    }

    [Fact]
    public void ReleaseRequirements_Block_Missing_Required_Testpoints()
    {
        using var fixture=CopyTutorial();File.WriteAllText(Path.Combine(fixture.Path,"requirements.md"),"Testpoints required.");
        var runtime=PCBHelperRuntime.ForCli();var result=runtime.Releases.ValidateRequirements(fixture.Path);
        Assert.True(result.Success);Assert.False(result.Data!.Passed);Assert.Contains(result.Data.Checks,c=>c.Id=="testpoints"&&c.Required&&!c.Implemented);
    }
    private static TempDirectory CopyTutorial(){var t=new TempDirectory();var s=Path.Combine(RepoRoot.Path,"fixtures","kicad-getting-started-led");foreach(var f in Directory.GetFiles(s))File.Copy(f,Path.Combine(t.Path,Path.GetFileName(f)));return t;}

    [Fact]
    public void RefillZones_Backend_Uses_Bounded_External_Process_Execution()
    {
        var repositoryRoot=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..",".."));
        var source=File.ReadAllText(Path.Combine(repositoryRoot,"src","PCBHelper.Core","KiCadZoneRefillBackend.cs"));

        Assert.Contains("TimeSpan.FromSeconds(30)",source);
        Assert.Contains("process.Kill(entireProcessTree: true)",source);
        Assert.Contains("TimedOut()",source);
    }

    private sealed class FakeZoneRefillBackend(bool succeed) : IKiCadZoneRefillBackend
    {
        public Task<ZoneRefillBackendResult> RefillAsync(string boardPath,string evidenceDirectory,CancellationToken cancellationToken)
        {
            File.AppendAllText(boardPath,Environment.NewLine+"\t(filled_polygon (layer \"B.Cu\") (island) (pts))");
            return Task.FromResult(new ZoneRefillBackendResult(
                succeed,
                succeed?0:1,
                "fake stdout",
                succeed?string.Empty:"fake failure",
                "fake-python"));
        }
    }
}
