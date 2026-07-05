namespace PCBHelper.E2E.Tests;

internal sealed class TestFixture : IDisposable
{
    private TestFixture(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static TestFixture CopyMinimalBoard()
    {
        return CopyFixture("minimal-board");
    }

    public static TestFixture CopyTutorialBoard()
    {
        return CopyFixture("kicad-getting-started-led");
    }

    public static TestFixture CopyBlankAuthoring()
    {
        return CopyFixture("blank-authoring");
    }

    private static TestFixture CopyFixture(string fixtureName)
    {
        var source = System.IO.Path.Combine(RepoRoot.Path, "fixtures", fixtureName);
        var destination = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcbhelper-e2e", Guid.NewGuid().ToString("N"));
        CopyDirectory(source, destination);
        return new TestFixture(destination);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(file)));
        }
    }
}
