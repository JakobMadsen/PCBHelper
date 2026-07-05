using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class RoutingServiceTests
{
    [Fact]
    public void Parser_Reads_Tracks_And_Vias()
    {
        using var fixture = CopyTutorialFixture();
        var service = new RoutingService(new ProjectDiscoveryService());

        var tracks = service.ListTracks(fixture.Path);
        var vias = service.ListVias(fixture.Path);

        Assert.True(tracks.Success);
        Assert.True(tracks.Data!.Tracks.Count >= 4);
        Assert.Contains(tracks.Data.Tracks, track => track.NetName == "LED_A" && track.Layer == "F.Cu" && track.WidthMillimeters == 0.5);
        Assert.True(vias.Success);
        Assert.True(vias.Data!.Vias.Count >= 2);
        Assert.Contains(vias.Data.Vias, via => via.NetName == "GND" && via.DrillMillimeters == 0.6);
    }

    [Fact]
    public void GetNetRouting_Returns_Pads_Tracks_And_Vias()
    {
        using var fixture = CopyTutorialFixture();
        var service = new RoutingService(new ProjectDiscoveryService());

        var result = service.GetNetRouting(fixture.Path, "LED_A");

        Assert.True(result.Success);
        Assert.Equal("LED_A", result.Data!.Net.Name);
        Assert.NotEmpty(result.Data.Pads);
        Assert.NotEmpty(result.Data.Tracks);
        Assert.Empty(result.Data.Vias);
        Assert.Contains(result.Data.Pads, pad => pad.FootprintReference == "R1" && pad.AbsoluteXMillimeters is not null);
    }

    [Fact]
    public void AddTrack_DryRun_Does_Not_Change_Board_File()
    {
        using var fixture = CopyTutorialFixture();
        var boardFile = Path.Combine(fixture.Path, "kicad-getting-started-led.kicad_pcb");
        var before = File.ReadAllText(boardFile);
        var service = new RoutingService(new ProjectDiscoveryService());

        var result = service.AddTrack(fixture.Path, "LED_A", 10, 10, 20, 10, "F.Cu", 0.25, dryRun: true);

        Assert.True(result.Success);
        Assert.True(result.Data!.DryRun);
        Assert.Equal(before, File.ReadAllText(boardFile));
        Assert.Contains("(segment", result.Data.Item.AfterText);
    }

    [Fact]
    public void AddTrack_Real_Inserts_One_Top_Level_Segment()
    {
        using var fixture = CopyTutorialFixture();
        var service = new RoutingService(new ProjectDiscoveryService());
        var before = service.ListTracks(fixture.Path).Data!.Tracks.Count;

        var result = service.AddTrack(fixture.Path, "LED_A", 10, 10, 20, 10, "F.Cu", 0.25, dryRun: false);
        var after = service.ListTracks(fixture.Path);

        Assert.True(result.Success);
        Assert.Equal(before + 1, after.Data!.Tracks.Count);
        Assert.Contains(after.Data.Tracks, track => track.Id == result.Data!.Item.Id && track.NetName == "LED_A");
    }

    [Fact]
    public void DeleteTrack_DryRun_Does_Not_Change_Board_File()
    {
        using var fixture = CopyTutorialFixture();
        var boardFile = Path.Combine(fixture.Path, "kicad-getting-started-led.kicad_pcb");
        var before = File.ReadAllText(boardFile);
        var service = new RoutingService(new ProjectDiscoveryService());
        var track = service.ListTracks(fixture.Path).Data!.Tracks.First().Id;

        var result = service.DeleteTrack(fixture.Path, track, dryRun: true);

        Assert.True(result.Success);
        Assert.True(result.Data!.DryRun);
        Assert.Equal(before, File.ReadAllText(boardFile));
        Assert.Contains("(segment", result.Data.Item.BeforeText);
    }

    [Fact]
    public void DeleteTrack_Real_Removes_Only_Selected_Segment()
    {
        using var fixture = CopyTutorialFixture();
        var service = new RoutingService(new ProjectDiscoveryService());
        var beforeTracks = service.ListTracks(fixture.Path).Data!.Tracks;
        var track = beforeTracks.First().Id;

        var result = service.DeleteTrack(fixture.Path, track, dryRun: false);
        var after = service.ListTracks(fixture.Path);

        Assert.True(result.Success);
        Assert.Equal(beforeTracks.Count - 1, after.Data!.Tracks.Count);
        Assert.DoesNotContain(after.Data.Tracks, item => item.Id == track);
    }

    [Fact]
    public void AddVia_And_DeleteVia_Work()
    {
        using var fixture = CopyTutorialFixture();
        var service = new RoutingService(new ProjectDiscoveryService());
        var before = service.ListVias(fixture.Path).Data!.Vias.Count;

        var add = service.AddVia(fixture.Path, "GND", 30, 30, 1.2, 0.6, "F.Cu,B.Cu", dryRun: false);
        var afterAdd = service.ListVias(fixture.Path);
        var delete = service.DeleteVia(fixture.Path, add.Data!.Item.Id, dryRun: false);
        var afterDelete = service.ListVias(fixture.Path);

        Assert.True(add.Success);
        Assert.Equal(before + 1, afterAdd.Data!.Vias.Count);
        Assert.True(delete.Success);
        Assert.Equal(before, afterDelete.Data!.Vias.Count);
    }

    [Theory]
    [InlineData("NOPE", "F.Cu", 0.25, "NET_NOT_FOUND")]
    [InlineData("LED_A", "In1.Cu", 0.25, "UNSUPPORTED_LAYER")]
    [InlineData("LED_A", "F.Cu", -1, "INVALID_ROUTING_GEOMETRY")]
    public void AddTrack_Returns_Stable_Errors(string net, string layer, double width, string code)
    {
        using var fixture = CopyTutorialFixture();
        var service = new RoutingService(new ProjectDiscoveryService());

        var result = service.AddTrack(fixture.Path, net, 10, 10, 20, 10, layer, width, dryRun: true);

        Assert.False(result.Success);
        Assert.Equal(code, result.Error?.Code);
    }

    [Fact]
    public async Task RestoreChange_Restores_Add_And_Delete_Routing_Changes()
    {
        using var fixture = CopyTutorialFixture();
        var projectDiscovery = new ProjectDiscoveryService();
        var reports = new ChangeReportService(projectDiscovery);
        var routing = new RoutingWorkflowService(new RoutingService(projectDiscovery), CreateCheckRunner(projectDiscovery), reports);
        var geometry = new GeometryWorkflowService(new GeometryService(projectDiscovery), CreateCheckRunner(projectDiscovery), reports);
        var values = new ComponentValueWorkflowService(new ComponentService(projectDiscovery), CreateCheckRunner(projectDiscovery), reports);
        var review = new ChangeReviewService(projectDiscovery, reports, geometry, values, routing);
        var initialTracks = routing.ListTracks(fixture.Path).Data!.Tracks.Count;

        var add = await routing.AddTrackAsync(fixture.Path, "LED_A", 10, 10, 20, 10, "F.Cu", 0.25, dryRun: false);
        Assert.Equal(initialTracks + 1, routing.ListTracks(fixture.Path).Data!.Tracks.Count);
        var restoreAdd = await review.RestoreChangeAsync(fixture.Path, add.Data!.ChangeReportPath!, dryRun: false);
        Assert.True(restoreAdd.Success);
        Assert.Equal(initialTracks, routing.ListTracks(fixture.Path).Data!.Tracks.Count);

        var track = routing.ListTracks(fixture.Path).Data!.Tracks.First().Id;
        var delete = await routing.DeleteTrackAsync(fixture.Path, track, dryRun: false);
        Assert.Equal(initialTracks - 1, routing.ListTracks(fixture.Path).Data!.Tracks.Count);
        var restoreDelete = await review.RestoreChangeAsync(fixture.Path, delete.Data!.ChangeReportPath!, dryRun: false);
        Assert.True(restoreDelete.Success);
        Assert.Equal(initialTracks, routing.ListTracks(fixture.Path).Data!.Tracks.Count);
    }

    private static CheckRunner CreateCheckRunner(ProjectDiscoveryService projectDiscovery)
    {
        using var fakeCli = new TempFile("kicad-cli.exe", deleteOnDispose: false);
        return new CheckRunner(
            projectDiscovery,
            new KiCadCliLocator(name => name == "KICAD_CLI" ? fakeCli.Path : null),
            new FakeCommandRunner());
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

    private sealed class FakeCommandRunner : ICommandRunner
    {
        public async Task<CommandExecutionResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory,
            CancellationToken cancellationToken = default)
        {
            for (var index = 0; index < arguments.Count - 1; index++)
            {
                if (arguments[index] == "--output")
                {
                    await File.WriteAllTextAsync(arguments[index + 1], "[]", cancellationToken);
                }
            }

            return new CommandExecutionResult(0, string.Empty, string.Empty);
        }
    }

    private sealed class TempFile : IDisposable
    {
        private readonly bool _deleteOnDispose;

        public TempFile(string fileName, bool deleteOnDispose = true)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcbhelper-tests", Guid.NewGuid().ToString("N"), fileName);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, string.Empty);
            _deleteOnDispose = deleteOnDispose;
        }

        public string Path { get; }

        public void Dispose()
        {
            if (_deleteOnDispose && Directory.Exists(System.IO.Path.GetDirectoryName(Path)))
            {
                Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true);
            }
        }
    }
}
