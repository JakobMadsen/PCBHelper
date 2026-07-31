using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class EngineeringGateServiceTests
{
    [Theory]
    [InlineData("warning", "test", EngineeringGateStatus.Passed, EngineeringGateCheckStatus.Passed)]
    [InlineData("warning", "lib_symbol_issues", EngineeringGateStatus.FindingsPresent, EngineeringGateCheckStatus.FindingsPresent)]
    [InlineData("error", "test", EngineeringGateStatus.FindingsPresent, EngineeringGateCheckStatus.FindingsPresent)]
    public async Task KiCad_Gate_Blocks_Errors_But_Records_Warnings(
        string severity,
        string type,
        EngineeringGateStatus expectedGateStatus,
        EngineeringGateCheckStatus expectedDrcStatus)
    {
        using var fixture = CopyFixture();
        using var fakeRoot = new TempDirectory();
        var fakeCli = Path.Combine(fakeRoot.Path, "kicad-cli.exe");
        File.WriteAllText(fakeCli, string.Empty);
        var projects = new ProjectDiscoveryService();
        var locator = new KiCadCliLocator(name => name == "KICAD_CLI" ? fakeCli : null);
        var runner = new FindingCommandRunner(severity, type);
        var summaries = new CheckSummaryService(new CheckRunner(projects, locator, runner));
        var exports = new ExportService(projects, locator, runner);
        var assembly = new AssemblyService(projects, new KiCadDoctorService(locator, runner), exports);
        var gates = new EngineeringGateService(summaries, assembly);

        var result = await gates.RunAsync(
            fixture.Path,
            new EngineeringGateRequirements("required", "required", "skip", "skip", "skip"));

        Assert.True(result.Success);
        Assert.Equal(expectedGateStatus, result.Data!.Status);
        var drc = Assert.Single(result.Data.Checks, check => check.Kind == "drc");
        Assert.Equal(expectedDrcStatus, drc.Status);
        Assert.Equal(1, drc.FindingCount);
    }

    private static TempDirectory CopyFixture()
    {
        var temp = new TempDirectory();
        var source = Path.Combine(RepoRoot.Path, "fixtures", "blank-authoring");
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(temp.Path, Path.GetFileName(file)));
        return temp;
    }

    private sealed class FindingCommandRunner(string severity, string type) : ICommandRunner
    {
        public async Task<CommandExecutionResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory,
            CancellationToken cancellationToken = default)
        {
            for (var index = 0; index < arguments.Count - 1; index++)
            {
                if (arguments[index] != "--output")
                    continue;

                var report = arguments.Contains("drc")
                    ? $$"""{"violations":[{"description":"reviewed diagnostic","severity":"{{severity}}","type":"{{type}}"}],"unconnected_items":[],"schematic_parity":[]}"""
                    : """{"violations":[]}""";
                await File.WriteAllTextAsync(arguments[index + 1], report, cancellationToken);
            }

            return new CommandExecutionResult(0, string.Empty, string.Empty);
        }
    }
}
