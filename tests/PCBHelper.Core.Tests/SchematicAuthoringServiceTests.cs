using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class SchematicAuthoringServiceTests
{
    [Fact]
    public void Parser_Reads_Symbols_Wires_And_Labels()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        service.CreateSymbol(fixture.Path, "Device:R", "R1", 50, 50, "330R", null, dryRun: false);
        service.CreateSymbol(fixture.Path, "Device:LED", "D1", 70, 50, null, null, dryRun: false);
        service.ConnectPins(fixture.Path, "R1.2", "D1.A", "LED_A", dryRun: false);
        service.AddNetLabel(fixture.Path, "VCC", 40, 50, dryRun: false);

        var result = service.ListSymbols(fixture.Path);

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.Symbols.Count);
        Assert.True(result.Data.WireCount >= 1);
        Assert.True(result.Data.LabelCount >= 2);
        Assert.Contains(result.Data.Symbols, symbol => symbol.Reference == "R1" && symbol.Value == "330R");
    }

    [Fact]
    public void DryRun_Mutations_Do_Not_Change_Files()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());
        var schematicFile = Path.Combine(fixture.Path, "blank-authoring.kicad_sch");
        var boardFile = Path.Combine(fixture.Path, "blank-authoring.kicad_pcb");
        var schematicBefore = File.ReadAllText(schematicFile);
        var boardBefore = File.ReadAllText(boardFile);

        var symbol = service.CreateSymbol(fixture.Path, "Device:R", "R1", 50, 50, "330R", null, dryRun: true);
        var label = service.AddNetLabel(fixture.Path, "VCC", 40, 50, dryRun: true);
        var update = service.UpdatePcbFromSchematic(fixture.Path, dryRun: true);

        Assert.True(symbol.Success);
        Assert.True(label.Success);
        Assert.True(update.Success);
        Assert.Equal(schematicBefore, File.ReadAllText(schematicFile));
        Assert.Equal(boardBefore, File.ReadAllText(boardFile));
    }

    [Fact]
    public void Real_Mutations_Create_Symbol_Field_Wire_Label_And_Board_Footprints()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        Assert.True(service.CreateSymbol(fixture.Path, "Device:Battery_Cell", "BT1", 30, 50, null, null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:R", "R1", 50, 50, "330R", null, dryRun: false).Success);
        Assert.True(service.CreateSymbol(fixture.Path, "Device:LED", "D1", 70, 50, null, null, dryRun: false).Success);
        Assert.True(service.SetSymbolField(fixture.Path, "R1", "Datasheet", "local", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "BT1.+", "R1.1", "VCC", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "R1.2", "D1.A", "LED_A", dryRun: false).Success);
        Assert.True(service.ConnectPins(fixture.Path, "D1.K", "BT1.-", "GND", dryRun: false).Success);
        Assert.True(service.AddNetLabel(fixture.Path, "GND", 60, 54, dryRun: false).Success);

        var update = service.UpdatePcbFromSchematic(fixture.Path, dryRun: false);
        var board = new BoardSummaryService(new ProjectDiscoveryService()).GetSummary(fixture.Path);
        var nets = new BoardInspectionService(new ProjectDiscoveryService()).ListNets(fixture.Path);

        Assert.True(update.Success);
        Assert.Contains(board.Data!.Footprints, footprint => footprint.Reference == "BT1");
        Assert.Contains(board.Data.Footprints, footprint => footprint.Reference == "R1");
        Assert.Contains(board.Data.Footprints, footprint => footprint.Reference == "D1");
        Assert.Contains(nets.Data!.Nets, net => net.Name == "LED_A");
    }

    [Fact]
    public async Task RestoreChange_Restores_Schematic_File_Snapshot()
    {
        using var fixture = CopyBlankFixture();
        var projectDiscovery = new ProjectDiscoveryService();
        var reports = new ChangeReportService(projectDiscovery);
        var checks = CreateCheckRunner(projectDiscovery);
        var schematic = new SchematicAuthoringWorkflowService(new SchematicAuthoringService(projectDiscovery), checks, reports);
        var review = new ChangeReviewService(
            projectDiscovery,
            reports,
            new GeometryWorkflowService(new GeometryService(projectDiscovery), checks, reports),
            new ComponentValueWorkflowService(new ComponentService(projectDiscovery), checks, reports),
            new RoutingWorkflowService(new RoutingService(projectDiscovery), checks, reports),
            schematic);

        var create = await schematic.CreateSymbolAsync(fixture.Path, "Device:R", "R1", 50, 50, "330R", null, dryRun: false);
        Assert.True(create.Success);
        Assert.Single(schematic.ListSymbols(fixture.Path).Data!.Symbols);

        var restore = await review.RestoreChangeAsync(fixture.Path, create.Data!.ChangeReportPath!, dryRun: false);

        Assert.True(restore.Success);
        Assert.Empty(schematic.ListSymbols(fixture.Path).Data!.Symbols);
    }

    [Fact]
    public void Stable_Errors_For_Unsupported_Symbol_Pin_And_Footprint()
    {
        using var fixture = CopyBlankFixture();
        var service = new SchematicAuthoringService(new ProjectDiscoveryService());

        var unsupported = service.CreateSymbol(fixture.Path, "Device:OpAmp", "U1", 50, 50, null, null, dryRun: true);
        service.CreateSymbol(fixture.Path, "Device:R", "R1", 50, 50, "330R", null, dryRun: false);
        var missingPin = service.ConnectPins(fixture.Path, "R1.9", "R1.1", null, dryRun: true);
        service.SetSymbolField(fixture.Path, "R1", "Footprint", "UnknownFootprint", dryRun: false);
        var missingFootprint = service.UpdatePcbFromSchematic(fixture.Path, dryRun: true);

        Assert.Equal("SCHEMATIC_SYMBOL_UNSUPPORTED", unsupported.Error?.Code);
        Assert.Equal("SCHEMATIC_PIN_NOT_FOUND", missingPin.Error?.Code);
        Assert.Equal("FOOTPRINT_TEMPLATE_NOT_FOUND", missingFootprint.Error?.Code);
    }

    private static CheckRunner CreateCheckRunner(ProjectDiscoveryService projectDiscovery)
    {
        using var fakeCli = new TempFile("kicad-cli.exe", deleteOnDispose: false);
        return new CheckRunner(
            projectDiscovery,
            new KiCadCliLocator(name => name == "KICAD_CLI" ? fakeCli.Path : null),
            new FakeCommandRunner());
    }

    private static TempDirectory CopyBlankFixture()
    {
        var temp = new TempDirectory();
        var source = Path.Combine(RepoRoot.Path, "fixtures", "blank-authoring");
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(temp.Path, Path.GetFileName(file)));
        }

        return temp;
    }

    private sealed class FakeCommandRunner : ICommandRunner
    {
        public async Task<CommandExecutionResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken = default)
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
