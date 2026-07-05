using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class KiCadVersionParserTests
{
    [Theory]
    [InlineData("9.0.9", 9, 0, 9)]
    [InlineData("KiCad 10.0.1", 10, 0, 1)]
    [InlineData("Application: KiCad x64 on x64\nVersion: 9.0.0-rc1", 9, 0, 0)]
    public void Parse_Returns_First_Version_Number(string text, int major, int minor, int patch)
    {
        var version = KiCadVersionParser.Parse(text);

        Assert.NotNull(version);
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Build);
    }

    [Fact]
    public void Parse_Returns_Null_When_No_Version_Exists()
    {
        Assert.Null(KiCadVersionParser.Parse("not a version"));
    }
}
