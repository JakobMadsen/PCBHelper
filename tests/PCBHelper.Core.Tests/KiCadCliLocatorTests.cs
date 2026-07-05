using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class KiCadCliLocatorTests
{
    [Fact]
    public void Locate_Uses_KicadCli_Environment_Variable_First()
    {
        using var temp = new TempDirectory();
        var executable = Path.Combine(temp.Path, OperatingSystem.IsWindows() ? "custom-kicad-cli.exe" : "custom-kicad-cli");
        File.WriteAllText(executable, string.Empty);

        var locator = new KiCadCliLocator(name => name switch
        {
            "KICAD_CLI" => executable,
            "PATH" => string.Empty,
            _ => null
        });

        var result = locator.Locate();

        Assert.True(result.Found);
        Assert.Equal(Path.GetFullPath(executable), result.ExecutablePath);
        Assert.Equal("KICAD_CLI", result.Source);
    }

    [Fact]
    public void Locate_Finds_KicadCli_On_Path()
    {
        using var temp = new TempDirectory();
        var executable = Path.Combine(temp.Path, OperatingSystem.IsWindows() ? "kicad-cli.exe" : "kicad-cli");
        File.WriteAllText(executable, string.Empty);

        var locator = new KiCadCliLocator(name => name switch
        {
            "KICAD_CLI" => null,
            "PATH" => temp.Path,
            _ => null
        });

        var result = locator.Locate();

        Assert.True(result.Found);
        Assert.Equal(Path.GetFullPath(executable), result.ExecutablePath);
        Assert.Equal("PATH", result.Source);
    }

    [Fact]
    public void Locate_Finds_KicadCli_In_Install_Root_Bin()
    {
        using var temp = new TempDirectory();
        var bin = Path.Combine(temp.Path, "bin");
        Directory.CreateDirectory(bin);
        var executable = Path.Combine(bin, OperatingSystem.IsWindows() ? "kicad-cli.exe" : "kicad-cli");
        File.WriteAllText(executable, string.Empty);

        var locator = new KiCadCliLocator(
            name => name switch
            {
                "KICAD_CLI" => null,
                "PATH" => string.Empty,
                _ => null
            },
            () => new[] { temp.Path });

        var result = locator.Locate();

        Assert.True(result.Found);
        Assert.Equal(Path.GetFullPath(executable), result.ExecutablePath);
        Assert.Equal("KiCad install location", result.Source);
    }

    [Fact]
    public void Locate_Returns_Missing_When_Not_Configured()
    {
        var locator = new KiCadCliLocator(_ => null, static () => Array.Empty<string>());

        var result = locator.Locate();

        Assert.False(result.Found);
        Assert.Null(result.ExecutablePath);
    }
}
