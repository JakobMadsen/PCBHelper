using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class ChangeReportAndWorkflowTests
{
    [Fact]
    public async Task ChangeReportService_Writes_And_Reads_Report()
    {
        using var fixture = CopyTutorialFixture();
        var service = new ChangeReportService(
            new ProjectDiscoveryService(),
            () => new DateTimeOffset(2026, 7, 5, 12, 30, 0, TimeSpan.Zero));

        var write = await service.WriteAsync(
            fixture.Path,
            new ChangeReportInput(
                "move",
                "D1",
                null,
                null,
                null,
                null,
                Path.Combine(fixture.Path, "kicad-getting-started-led.kicad_pcb"),
                new Placement(68, 35, 0),
                new Placement(75, 35, 0),
                "Checks ran.",
                new[] { "drc.json" }));

        Assert.True(write.Success);
        Assert.NotNull(write.Data);
        Assert.True(File.Exists(write.Data.ReportPath));
        Assert.StartsWith("20260705T123000000Z-move-d1-", write.Data.ChangeId, StringComparison.Ordinal);

        var read = service.Read(fixture.Path, write.Data.ChangeId);

        Assert.True(read.Success);
        Assert.Equal(write.Data.ChangeId, read.Data?.ChangeId);
        Assert.Equal("pcbhelper restore-change", read.Data?.RestoreCommand[..24]);
    }

    [Fact]
    public async Task RestoreChange_DryRun_Does_Not_Change_Board()
    {
        using var fixture = CopyTutorialFixture();
        var workflow = CreateWorkflow();
        var move = await workflow.MoveComponentAsync(fixture.Path, "D1", 75, 35, dryRun: false);
        var boardFile = Path.Combine(fixture.Path, "kicad-getting-started-led.kicad_pcb");
        var movedText = File.ReadAllText(boardFile);

        var restore = await workflow.RestoreChangeAsync(fixture.Path, move.Data!.ChangeReportPath!, dryRun: true);

        Assert.True(restore.Success);
        Assert.Equal(movedText, File.ReadAllText(boardFile));
        Assert.True(restore.Data?.DryRun);
        Assert.Null(restore.Data?.ChangeReportPath);
        Assert.Equal(68, restore.Data?.After.XMillimeters);
    }

    [Fact]
    public async Task RestoreChange_Real_Move_Restores_Position_And_Writes_New_Report()
    {
        using var fixture = CopyTutorialFixture();
        var workflow = CreateWorkflow();
        var move = await workflow.MoveComponentAsync(fixture.Path, "D1", 75, 35, dryRun: false);

        var restore = await workflow.RestoreChangeAsync(fixture.Path, move.Data!.ChangeReportPath!, dryRun: false);
        var summary = new BoardSummaryService(new ProjectDiscoveryService()).GetSummary(fixture.Path);
        var d1 = summary.Data!.Footprints.Single(footprint => footprint.Reference == "D1");

        Assert.True(restore.Success);
        Assert.NotNull(restore.Data?.ChangeReportPath);
        Assert.True(File.Exists(restore.Data.ChangeReportPath));
        Assert.Equal(68, d1.XMillimeters);
        Assert.Equal(35, d1.YMillimeters);
    }

    private static GeometryWorkflowService CreateWorkflow()
    {
        var projectDiscovery = new ProjectDiscoveryService();
        var geometry = new GeometryService(projectDiscovery);
        var checks = new CheckRunner(projectDiscovery, new KiCadCliLocator(_ => null), new FakeCommandRunner());
        var reports = new ChangeReportService(projectDiscovery);
        return new GeometryWorkflowService(geometry, checks, reports);
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
        public Task<CommandExecutionResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CommandExecutionResult(0, string.Empty, string.Empty));
        }
    }
}
