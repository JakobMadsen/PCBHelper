using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class ProjectTransactionServiceTests
{
    [Fact]
    public async Task Failure_On_Second_Write_Rolls_Back_First_File()
    {
        using var project = CreateProject();
        var discovery = new ProjectDiscoveryService();
        var writer = new FailOnSecondWriteWriter();
        var service = new ProjectTransactionService(discovery, new ProjectTransactionStore(discovery), writer, () => DateTimeOffset.UtcNow);
        var a = Path.Combine(project.Path, "a.kicad_sch");
        var b = Path.Combine(project.Path, "b.kicad_pcb");
        var changes = new[] { PreparedFileChange.Create("a.kicad_sch", "a-before", "a-after"), PreparedFileChange.Create("b.kicad_pcb", "b-before", "b-after") };

        var result = await service.ApplyAsync(project.Path, "test rollback", "hash", Array.Empty<PreparedOperation>(), changes);

        Assert.False(result.Success);
        Assert.Equal("TRANSACTION_APPLY_FAILED", result.Error?.Code);
        Assert.Equal(ProjectTransactionStatus.RolledBack, result.Data?.Transaction.Status);
        Assert.Equal("a-before", File.ReadAllText(a));
        Assert.Equal("b-before", File.ReadAllText(b));
    }

    [Fact]
    public async Task Stale_Before_Hash_Prevents_All_Writes()
    {
        using var project = CreateProject();
        var discovery = new ProjectDiscoveryService();
        var service = new ProjectTransactionService(discovery);
        var changes = new[] { PreparedFileChange.Create("a.kicad_sch", "not-current", "after") };

        var result = await service.ApplyAsync(project.Path, "stale", "hash", Array.Empty<PreparedOperation>(), changes);

        Assert.False(result.Success);
        Assert.Equal("TRANSACTION_CONFLICT", result.Error?.Code);
        Assert.Equal("a-before", File.ReadAllText(Path.Combine(project.Path, "a.kicad_sch")));
    }

    private static TempDirectory CreateProject()
    {
        var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "test.kicad_pro"), "{}");
        File.WriteAllText(Path.Combine(temp.Path, "a.kicad_sch"), "a-before");
        File.WriteAllText(Path.Combine(temp.Path, "b.kicad_pcb"), "b-before");
        return temp;
    }

    private sealed class FailOnSecondWriteWriter : IProjectFileWriter
    {
        private int _calls;
        public async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken = default)
        {
            _calls++;
            if (_calls == 2) throw new IOException("simulated second write failure");
            await File.WriteAllTextAsync(path, content, cancellationToken);
        }
    }
}
