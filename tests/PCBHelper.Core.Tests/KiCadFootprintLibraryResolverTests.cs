namespace PCBHelper.Core.Tests;

public sealed class KiCadFootprintLibraryResolverTests
{
    [Fact]
    public void Resolve_FindsFootprintInConfiguredStandardLibraryRoot()
    {
        using var fixture = new TempDirectory();
        var library = Path.Combine(fixture.Path, "Package_SO.pretty");
        Directory.CreateDirectory(library);
        var expected = Path.Combine(library, "TSSOP-14_4.4x5mm_P0.65mm.kicad_mod");
        File.WriteAllText(expected, "(footprint \"TSSOP-14_4.4x5mm_P0.65mm\")");

        var actual = KiCadFootprintLibraryResolver.Resolve(
            "Package_SO:TSSOP-14_4.4x5mm_P0.65mm",
            fixture.Path);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Resolve_SearchesEveryConfiguredStandardLibraryRoot()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        var library = Path.Combine(second.Path, "Package_TO_SOT_SMD.pretty");
        Directory.CreateDirectory(library);
        var expected = Path.Combine(library, "SOT-23-5.kicad_mod");
        File.WriteAllText(expected, "(footprint \"SOT-23-5\")");
        var roots = string.Join(Path.PathSeparator, first.Path, second.Path);

        var actual = KiCadFootprintLibraryResolver.Resolve(
            "Package_TO_SOT_SMD:SOT-23-5",
            roots);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GetSearchRoots_ConfiguredRootsOverrideMachineDefaults()
    {
        using var fixture = new TempDirectory();

        var roots = KiCadFootprintLibraryResolver.GetSearchRoots(fixture.Path);

        Assert.Equal(new[] { fixture.Path }, roots);
    }
}
