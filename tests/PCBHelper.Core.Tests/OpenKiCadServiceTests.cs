using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class OpenKiCadServiceTests
{
    [Fact]
    public void KiCadExecutableLocator_Finds_Kicad_Next_To_KicadCli()
    {
        using var temp = new TempDirectory();
        var cliPath = Path.Combine(temp.Path, OperatingSystem.IsWindows() ? "kicad-cli.exe" : "kicad-cli");
        var kicadPath = Path.Combine(temp.Path, OperatingSystem.IsWindows() ? "kicad.exe" : "kicad");
        File.WriteAllText(cliPath, string.Empty);
        File.WriteAllText(kicadPath, string.Empty);

        var cliLocator = new KiCadCliLocator(name => name == "KICAD_CLI" ? cliPath : null);
        var locator = new KiCadExecutableLocator(cliLocator, _ => null);

        var result = locator.Locate();

        Assert.True(result.Found);
        Assert.Equal(Path.GetFullPath(kicadPath), result.ExecutablePath);
    }

    [Fact]
    public void OpenProject_DryRun_Does_Not_Start_Process()
    {
        using var fixture = CopyTutorialFixture();
        using var temp = new TempDirectory();
        var cliPath = Path.Combine(temp.Path, OperatingSystem.IsWindows() ? "kicad-cli.exe" : "kicad-cli");
        var kicadPath = Path.Combine(temp.Path, OperatingSystem.IsWindows() ? "kicad.exe" : "kicad");
        File.WriteAllText(cliPath, string.Empty);
        File.WriteAllText(kicadPath, string.Empty);

        var cliLocator = new KiCadCliLocator(name => name == "KICAD_CLI" ? cliPath : null);
        var service = new OpenKiCadService(
            new ProjectDiscoveryService(),
            new KiCadExecutableLocator(cliLocator, _ => null),
            new FakeStarter());

        var result = service.OpenProject(fixture.Path, dryRun: true);

        Assert.True(result.Success);
        Assert.True(result.Data?.DryRun);
        Assert.False(result.Data?.Started);
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

    private sealed class FakeStarter : IProcessStarter
    {
        public StartedProcessResult Start(string fileName, IReadOnlyList<string> arguments, string? workingDirectory)
        {
            throw new InvalidOperationException("Dry-run should not start a process.");
        }
    }
}
