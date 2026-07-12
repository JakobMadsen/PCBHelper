using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class ComponentInspectionServiceTests
{
    [Fact]
    public void GetValue_Reads_R1_Board_Value()
    {
        using var fixture = CopyTutorialFixture();
        var service = new ComponentService(new ProjectDiscoveryService());

        var result = service.GetValue(fixture.Path, "R1");

        Assert.True(result.Success);
        Assert.Equal("R1", result.Data?.Reference);
        Assert.Contains(result.Data!.Locations, location => location.Source == "board" && location.Value == "330R");
    }

    [Fact]
    public void SetValue_DryRun_Does_Not_Change_Files()
    {
        using var fixture = CopyTutorialFixture();
        var boardFile = Path.Combine(fixture.Path, "kicad-getting-started-led.kicad_pcb");
        var beforeText = File.ReadAllText(boardFile);
        var service = new ComponentService(new ProjectDiscoveryService());

        var result = service.SetValue(fixture.Path, "R1", "300R", "available", dryRun: true);

        Assert.True(result.Success);
        Assert.True(result.Data?.DryRun);
        Assert.Equal(beforeText, File.ReadAllText(boardFile));
        Assert.Contains(result.Data!.Before, location => location.Value == "330R");
        Assert.Contains(result.Data.After, location => location.Value == "300R");
    }

    [Fact]
    public void SetValue_Real_Updates_Only_R1_Value()
    {
        using var fixture = CopyTutorialFixture();
        var service = new ComponentService(new ProjectDiscoveryService());

        var result = service.SetValue(fixture.Path, "R1", "300R", "available", dryRun: false);
        var r1 = service.GetValue(fixture.Path, "R1");
        var d1 = service.GetValue(fixture.Path, "D1");

        Assert.True(result.Success);
        Assert.False(result.Data?.DryRun);
        Assert.Contains(r1.Data!.Locations, location => location.Value == "300R");
        Assert.Contains(d1.Data!.Locations, location => location.Value == "LED");
    }

    [Fact]
    public async Task RestoreChange_Restores_Value_Change()
    {
        using var fixture = CopyTutorialFixture();
        var projectDiscovery = new ProjectDiscoveryService();
        var componentService = new ComponentService(projectDiscovery);
        var changeReports = new ChangeReportService(projectDiscovery);
        var valueWorkflow = new ComponentValueWorkflowService(componentService, CreateCheckRunner(projectDiscovery), changeReports);
        var geometryWorkflow = new GeometryWorkflowService(new GeometryService(projectDiscovery), CreateCheckRunner(projectDiscovery), changeReports);
        var review = new ChangeReviewService(projectDiscovery, changeReports, geometryWorkflow, valueWorkflow);

        var set = await valueWorkflow.SetValueAsync(fixture.Path, "R1", "300R", "available", dryRun: false);
        var restore = await review.RestoreChangeAsync(fixture.Path, set.Data!.ChangeReportPath!, dryRun: false);
        var value = componentService.GetValue(fixture.Path, "R1");

        Assert.True(set.Success);
        Assert.True(restore.Success);
        Assert.Contains(value.Data!.Locations, location => location.Value == "330R");
    }

    [Fact]
    public void BoardInspection_Reads_Nets_And_Footprint_Pads()
    {
        using var fixture = CopyTutorialFixture();
        var service = new BoardInspectionService(new ProjectDiscoveryService());

        var nets = service.ListNets(fixture.Path);
        var ledA = service.GetNet(fixture.Path, "LED_A");
        var pads = service.ListFootprintPads(fixture.Path, "R1");

        Assert.True(nets.Success);
        Assert.Equal(new[] { 1, 2, 3 }, nets.Data!.Nets.Select(static net => net.Code).ToArray());
        Assert.True(ledA.Success);
        Assert.Contains(ledA.Data!.Pads, pad => pad.FootprintReference == "R1" && pad.PadName == "2" && pad.PadLayers.Count > 0);
        Assert.True(pads.Success);
        Assert.Contains(pads.Data!.Pads, pad => pad.NetName == "VCC");
    }

    [Fact]
    public void BoardInspection_Reads_KiCad10_Named_Net_References()
    {
        using var fixture = new TempDirectory();
        File.WriteAllText(Path.Combine(fixture.Path, "mixed.kicad_pro"), "{}");
        File.WriteAllText(
            Path.Combine(fixture.Path, "mixed.kicad_pcb"),
            """
            (kicad_pcb
              (version 20250114)
              (generator "PCBHelper.Tests")
              (net 1 "GND")
              (footprint "Test:R"
                (layer "F.Cu")
                (uuid "11111111-1111-1111-1111-111111111111")
                (at 10 10)
                (property "Reference" "R1")
                (pad "1" smd rect
                  (at 0 0)
                  (size 1 1)
                  (layers "F.Cu")
                  (net "VCC")
                  (pinfunction "1"))
                (pad "2" smd rect
                  (at 2 0)
                  (size 1 1)
                  (layers "F.Cu")
                  (net 1 "GND")
                  (pinfunction "2"))
                (pad "3" smd rect
                  (at 4 0)
                  (size 1 1)
                  (layers "F.Cu")
                  (net ""))
              )
              (segment
                (start 10 10)
                (end 20 10)
                (width 0.25)
                (layer "F.Cu")
                (net "TIA_IN")
                (uuid "22222222-2222-2222-2222-222222222222"))
              (via
                (at 15 15)
                (size 0.8)
                (drill 0.4)
                (layers "F.Cu" "B.Cu")
                (net "VCC")
                (uuid "33333333-3333-3333-3333-333333333333"))
            )
            """);
        var service = new BoardInspectionService(new ProjectDiscoveryService());

        var nets = service.ListNets(fixture.Path);
        var vcc = service.GetNet(fixture.Path, "VCC");
        var tiaIn = service.GetNet(fixture.Path, "TIA_IN");

        Assert.True(nets.Success);
        Assert.Contains(nets.Data!.Nets, net => net.Name == "GND" && net.PadCount == 1);
        Assert.Contains(nets.Data.Nets, net => net.Name == "VCC" && net.PadCount == 1 && net.ViaCount == 1);
        Assert.Contains(nets.Data.Nets, net => net.Name == "TIA_IN" && net.TrackCount == 1);
        Assert.DoesNotContain(nets.Data.Nets, net => string.IsNullOrWhiteSpace(net.Name));
        Assert.True(vcc.Success);
        Assert.Single(vcc.Data!.Pads);
        Assert.True(tiaIn.Success);
        Assert.Equal(1, tiaIn.Data!.TrackCount);
    }

    [Fact]
    public async Task GuiCapabilities_Returns_Stable_Unavailable_Result_When_Ipc_Is_Missing()
    {
        using var fixture = CopyTutorialFixture();
        using var fakeCli = new TempFile("kicad-cli.exe");
        var service = new GuiReviewService(
            new KiCadCliLocator(name => name == "KICAD_CLI" ? fakeCli.Path : null),
            new KiCadExecutableLocator(new KiCadCliLocator(name => name == "KICAD_CLI" ? fakeCli.Path : null)),
            new StaticCommandRunner("--help output with pcb and sch commands only"));

        var capabilities = await service.GetCapabilitiesAsync(fixture.Path);
        var refresh = await service.RefreshProjectAsync(fixture.Path);

        Assert.True(capabilities.Success);
        Assert.False(capabilities.Data!.CanRefreshLive);
        Assert.False(refresh.Success);
        Assert.Equal("KICAD_IPC_UNAVAILABLE", refresh.Error?.Code);
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
            var outputIndex = -1;
            for (var index = 0; index < arguments.Count; index++)
            {
                if (arguments[index] == "--output")
                {
                    outputIndex = index;
                    break;
                }
            }

            if (outputIndex >= 0 && outputIndex < arguments.Count - 1)
            {
                await File.WriteAllTextAsync(arguments[outputIndex + 1], "[]", cancellationToken);
            }

            return new CommandExecutionResult(0, string.Empty, string.Empty);
        }
    }

    private sealed class StaticCommandRunner : ICommandRunner
    {
        private readonly string _stdout;

        public StaticCommandRunner(string stdout)
        {
            _stdout = stdout;
        }

        public Task<CommandExecutionResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CommandExecutionResult(0, _stdout, string.Empty));
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
