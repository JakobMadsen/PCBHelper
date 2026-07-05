using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class ExportPathFactoryTests
{
    [Fact]
    public void CreateExportDirectory_Creates_Timestamped_Directory_Under_Project()
    {
        using var temp = new TempDirectory();

        var directory = ExportPathFactory.CreateExportDirectory(temp.Path);

        Assert.True(Directory.Exists(directory));
        Assert.StartsWith(Path.Combine(temp.Path, ".pcbhelper", "exports"), directory);
    }
}
