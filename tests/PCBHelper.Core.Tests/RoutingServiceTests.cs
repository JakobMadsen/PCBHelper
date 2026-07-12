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
    public void Parser_Reads_Pad_Size_And_Shape()
    {
        using var fixture = CopyTutorialFixture();
        var pads = new BoardInspectionService(new ProjectDiscoveryService()).ListFootprintPads(fixture.Path, "R1");

        Assert.True(pads.Success);
        Assert.Contains(pads.Data!.Pads, pad => pad.Name == "1" && pad.Shape == "circle" && pad.SizeXMillimeters == 1.8 && pad.SizeYMillimeters == 1.8);
    }

    [Fact]
    public void ListUnroutedConnections_Finds_And_Clears_Filtered_Net()
    {
        using var fixture = CopyTutorialFixture();
        var service = new RoutingService(new ProjectDiscoveryService());

        var before = service.ListUnroutedConnections(fixture.Path, "LED_A");
        var add = service.AddTrackPolyline(fixture.Path, "LED_A", "55.16,35;68.73,35", "F.Cu", 0.25, dryRun: false);
        var after = service.ListUnroutedConnections(fixture.Path, "LED_A");

        Assert.True(before.Success);
        Assert.Single(before.Data!.Nets);
        Assert.True(add.Success);
        Assert.Empty(after.Data!.Nets);
    }

    [Fact]
    public void ValidateTrackClearance_Allows_Clear_Same_Net_And_Rejects_Foreign_Pad()
    {
        using var fixture = CopyTutorialFixture();
        var service = new RoutingService(new ProjectDiscoveryService());

        var clear = service.ValidateTrackClearance(fixture.Path, "LED_A", "55.16,35;68.73,35", "F.Cu", 0.25);
        var violation = service.ValidateTrackClearance(fixture.Path, "LED_A", "70,50;76,50", "B.Cu", 0.25);

        Assert.True(clear.Success);
        Assert.False(violation.Success);
        Assert.Equal("ROUTING_CLEARANCE_VIOLATION", violation.Error?.Code);
    }

    [Fact]
    public void AddTrackPolyline_Is_Atomic_When_Clearance_Fails()
    {
        using var fixture = CopyTutorialFixture();
        var service = new RoutingService(new ProjectDiscoveryService());
        var before = service.ListTracks(fixture.Path).Data!.Tracks.Count;

        var result = service.AddTrackPolyline(fixture.Path, "LED_A", "10,10;20,10;70,50;76,50", "B.Cu", 0.25, dryRun: false);
        var after = service.ListTracks(fixture.Path).Data!.Tracks.Count;

        Assert.False(result.Success);
        Assert.Equal("ROUTING_CLEARANCE_VIOLATION", result.Error?.Code);
        Assert.Equal(before, after);
    }

    [Fact]
    public void AddTrackPolyline_Writes_Multiple_Segments()
    {
        using var fixture = CopyTutorialFixture();
        var service = new RoutingService(new ProjectDiscoveryService());
        var before = service.ListTracks(fixture.Path).Data!.Tracks.Count;

        var result = service.AddTrackPolyline(fixture.Path, "LED_A", "10,10;20,10;20,20", "F.Cu", 0.25, dryRun: false);
        var after = service.ListTracks(fixture.Path).Data!.Tracks.Count;

        Assert.True(result.Success);
        Assert.Equal("track-polyline", result.Data!.Item.Kind);
        Assert.Equal(before + 2, after);
    }

    [Fact]
    public void AddVia_Rejects_Foreign_Net_Copper()
    {
        using var fixture = CopyTutorialFixture();
        var service = new RoutingService(new ProjectDiscoveryService());

        var result = service.AddVia(fixture.Path, "LED_A", 73, 50, 1.2, 0.6, "F.Cu,B.Cu", dryRun: true);

        Assert.False(result.Success);
        Assert.Equal("ROUTING_CLEARANCE_VIOLATION", result.Error?.Code);
    }

    [Fact]
    public async Task AutorouteBoard_Returns_Stable_Unavailable_When_FreeRouting_Is_Not_Configured()
    {
        using var fixture = CopyTutorialFixture();
        using var fakeCli = new TempFile("kicad-cli.exe", deleteOnDispose: false);
        using var emptyToolsRoot = new TempDirectory();
        var service = new AutoroutingService(
            new ProjectDiscoveryService(),
            new KiCadCliLocator(name => name == "KICAD_CLI" ? fakeCli.Path : null),
            new FreeRoutingLocator(_ => null, () => emptyToolsRoot.Path),
            new FakeCommandRunner());

        var result = await service.AutorouteBoardAsync(fixture.Path, dryRun: true);

        Assert.False(result.Success);
        Assert.Equal("ROUTING_BACKEND_UNAVAILABLE", result.Error?.Code);
    }

    [Fact]
    public void FreeRoutingLocator_Finds_Cached_Jar()
    {
        using var temp = new TempDirectory();
        var jar = Path.Combine(temp.Path, "v9.9.9", "freerouting-9.9.9.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(jar)!);
        File.WriteAllText(jar, "fake");
        var locator = new FreeRoutingLocator(_ => null, () => temp.Path);

        var result = locator.Locate();

        Assert.True(result.Found);
        Assert.Equal(jar, result.ExecutablePath);
        Assert.Equal(FreeRoutingExecutableType.Jar, result.ExecutableType);
        Assert.Equal("PCBHelper tools cache", result.Source);
    }

    [Fact]
    public async Task SetupFreeRouting_Downloads_Jar_To_Tools_Cache()
    {
        using var temp = new TempDirectory();
        var locator = new FreeRoutingLocator(_ => null, () => temp.Path);
        var service = new FreeRoutingSetupService(locator, new FakeFreeRoutingReleaseClient());

        var result = await service.SetupAsync(dryRun: false);

        Assert.True(result.Success);
        Assert.True(result.Data!.Installed);
        Assert.True(File.Exists(result.Data.TargetPath));
        Assert.True(locator.Locate().Found);
    }

    [Fact]
    public async Task AutorouteBoard_Uses_Headless_FreeRouting_With_Relative_Dsn_And_Ses()
    {
        using var fixture = CopyTutorialFixture();
        using var fakeCli = new TempFile("kicad-cli.exe", deleteOnDispose: false);
        using var fakeJar = new TempFile("freerouting-2.2.4.jar", deleteOnDispose: false);
        using var fakeJavaHome = new TempDirectory();
        var javaPath = Path.Combine(fakeJavaHome.Path, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        Directory.CreateDirectory(Path.GetDirectoryName(javaPath)!);
        File.WriteAllText(javaPath, string.Empty);
        var runner = new AutorouteRecordingCommandRunner();
        var service = new AutoroutingService(
            new ProjectDiscoveryService(),
            new KiCadCliLocator(name => name == "KICAD_CLI" ? fakeCli.Path : null),
            new FreeRoutingLocator(
                name => name switch
                {
                    "FREEROUTING_JAR" => fakeJar.Path,
                    "JAVA_HOME" => fakeJavaHome.Path,
                    _ => null
                }),
            runner,
            TimeSpan.FromSeconds(5));

        var result = await service.AutorouteBoardAsync(fixture.Path, dryRun: false);

        Assert.True(result.Success, result.Error?.Message);
        var routeCall = runner.Calls.Single(call => call.Arguments.Contains("-de"));
        Assert.Equal(javaPath, routeCall.FileName);
        Assert.Equal(result.Data!.RoutingRoot, routeCall.WorkingDirectory);
        Assert.Contains("-Djava.awt.headless=true", routeCall.Arguments);
        Assert.Contains("-jar", routeCall.Arguments);
        Assert.Contains(fakeJar.Path, routeCall.Arguments);
        Assert.Equal("board.dsn", routeCall.Arguments[Array.IndexOf(routeCall.Arguments.ToArray(), "-de") + 1]);
        Assert.Equal("board.ses", routeCall.Arguments[Array.IndexOf(routeCall.Arguments.ToArray(), "-do") + 1]);
    }

    [Fact]
    public async Task AutorouteBoard_Times_Out_With_Clear_Diagnostic_When_FreeRouting_Hangs()
    {
        using var fixture = CopyTutorialFixture();
        using var fakeCli = new TempFile("kicad-cli.exe", deleteOnDispose: false);
        using var fakeJar = new TempFile("freerouting-2.2.4.jar", deleteOnDispose: false);
        using var fakeJavaHome = new TempDirectory();
        var javaPath = Path.Combine(fakeJavaHome.Path, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        Directory.CreateDirectory(Path.GetDirectoryName(javaPath)!);
        File.WriteAllText(javaPath, string.Empty);
        var service = new AutoroutingService(
            new ProjectDiscoveryService(),
            new KiCadCliLocator(name => name == "KICAD_CLI" ? fakeCli.Path : null),
            new FreeRoutingLocator(
                name => name switch
                {
                    "FREEROUTING_JAR" => fakeJar.Path,
                    "JAVA_HOME" => fakeJavaHome.Path,
                    _ => null
                }),
            new HangingAutorouteCommandRunner(),
            TimeSpan.FromMilliseconds(50));

        var result = await service.AutorouteBoardAsync(fixture.Path, dryRun: false);

        Assert.False(result.Success);
        Assert.Equal("ROUTING_BACKEND_UNAVAILABLE", result.Error?.Code);
        Assert.Contains("timed out", result.Error?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("interactive", result.Error?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RoutingFixture_Reports_Known_Unrouted_Net()
    {
        using var fixture = CopyRoutingFixture();
        var service = new RoutingService(new ProjectDiscoveryService());

        var result = service.ListUnroutedConnections(fixture.Path, "A");

        Assert.True(result.Success);
        var net = Assert.Single(result.Data!.Nets);
        Assert.Equal("A", net.Net.Name);
        var missing = Assert.Single(net.MissingConnections);
        Assert.Equal("A1", missing.From.FootprintReference);
        Assert.Equal("A2", missing.To.FootprintReference);
    }

    [Fact]
    public void RoutingFixture_Rejects_Shortest_Path_Through_Foreign_Pad()
    {
        using var fixture = CopyRoutingFixture();
        var service = new RoutingService(new ProjectDiscoveryService());

        var result = service.ValidateTrackClearance(fixture.Path, "A", "10,10;30,10", "F.Cu", 0.25);

        Assert.False(result.Success);
        Assert.Equal("ROUTING_CLEARANCE_VIOLATION", result.Error?.Code);
        Assert.Contains("B1.1", result.Error?.Message);
    }

    [Fact]
    public void RoutingFixture_Allows_Dogleg_Around_Foreign_Pad_And_Connects_Net()
    {
        using var fixture = CopyRoutingFixture();
        var service = new RoutingService(new ProjectDiscoveryService());

        var add = service.AddTrackPolyline(fixture.Path, "A", "10,10;10,5;30,5;30,10", "F.Cu", 0.25, dryRun: false);
        var after = service.ListUnroutedConnections(fixture.Path, "A");

        Assert.True(add.Success);
        Assert.Equal("track-polyline", add.Data!.Item.Kind);
        Assert.Empty(after.Data!.Nets);
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

        var addPolyline = await routing.AddTrackPolylineAsync(fixture.Path, "LED_A", "10,10;20,10;20,20", "F.Cu", 0.25, dryRun: false);
        Assert.Equal(initialTracks + 2, routing.ListTracks(fixture.Path).Data!.Tracks.Count);
        var restorePolyline = await review.RestoreChangeAsync(fixture.Path, addPolyline.Data!.ChangeReportPath!, dryRun: false);
        Assert.True(restorePolyline.Success);
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

    private static TempDirectory CopyRoutingFixture()
    {
        var temp = new TempDirectory();
        var source = Path.Combine(RepoRoot.Path, "fixtures", "routing-primitives");
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

    private sealed class AutorouteRecordingCommandRunner : ICommandRunner
    {
        public List<CommandCall> Calls { get; } = new();

        public async Task<CommandExecutionResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new CommandCall(fileName, arguments.ToArray(), workingDirectory));
            for (var index = 0; index < arguments.Count - 1; index++)
            {
                if (arguments[index] == "--output")
                {
                    await File.WriteAllTextAsync(arguments[index + 1], "dsn", cancellationToken);
                }

                if (arguments[index] == "-do" && workingDirectory is not null)
                {
                    await File.WriteAllTextAsync(Path.Combine(workingDirectory, arguments[index + 1]), "ses", cancellationToken);
                }
            }

            return new CommandExecutionResult(0, string.Empty, string.Empty);
        }
    }

    private sealed class HangingAutorouteCommandRunner : ICommandRunner
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
                    await File.WriteAllTextAsync(arguments[index + 1], "dsn", cancellationToken);
                    return new CommandExecutionResult(0, string.Empty, string.Empty);
                }
            }

            if (arguments.Contains("-de"))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan);
            }

            return new CommandExecutionResult(0, string.Empty, string.Empty);
        }
    }

    private sealed record CommandCall(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory);

    private sealed class FakeFreeRoutingReleaseClient : IFreeRoutingReleaseClient
    {
        public Task<ToolResponse<FreeRoutingReleaseAsset>> GetLatestJarAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ToolResponse<FreeRoutingReleaseAsset>.Ok(
                "Found fake release.",
                new FreeRoutingReleaseAsset("v9.9.9", "freerouting-9.9.9.jar", new Uri("https://example.invalid/freerouting.jar"))));
        }

        public Task DownloadAsync(Uri downloadUrl, string targetPath, CancellationToken cancellationToken = default)
        {
            return File.WriteAllTextAsync(targetPath, "fake jar", cancellationToken);
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
