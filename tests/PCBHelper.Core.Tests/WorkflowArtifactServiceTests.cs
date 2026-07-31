namespace PCBHelper.Core.Tests;

public sealed class WorkflowArtifactServiceTests
{
    [Fact]
    public void Artifacts_Are_Content_Addressed_Bounded_And_Lock_Free()
    {
        using var fixture = CreateProject();
        var report = Path.Combine(fixture.Path, ".pcbhelper", "reports", "proof.json");
        var binary = Path.Combine(fixture.Path, ".pcbhelper", "reports", "image.bin");
        var locked = Path.Combine(fixture.Path, ".pcbhelper", "locks", "secret.json");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        Directory.CreateDirectory(Path.GetDirectoryName(locked)!);
        File.WriteAllText(report, "{\"passed\":true}");
        File.WriteAllBytes(binary, [1, 2, 3]);
        File.WriteAllText(locked, "secret");
        var module = CreateModule();

        var list = module.ListArtifacts(fixture.Path);
        var text = module.GetArtifact(fixture.Path, list.Data!.Artifacts.Single(item => item.RelativePath.EndsWith("proof.json")).ArtifactId);
        var binaryContent = module.GetArtifact(fixture.Path, list.Data.Artifacts.Single(item => item.RelativePath.EndsWith("image.bin")).ArtifactId);

        Assert.True(list.Success);
        Assert.Equal(2, list.Data.Artifacts.Count);
        Assert.Contains("passed", text.Data!.Text);
        Assert.Null(binaryContent.Data!.Text);
        Assert.All(list.Data.Artifacts, item => Assert.Equal(64, item.Sha256.Length));
    }

    [Fact]
    public void Large_Text_Is_Truncated_At_The_Module_Interface()
    {
        using var fixture = CreateProject();
        var path = Path.Combine(fixture.Path, ".pcbhelper", "reports", "large.log");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new string('x', 300_000));
        var module = CreateModule();
        var artifact = Assert.Single(module.ListArtifacts(fixture.Path).Data!.Artifacts);

        var content = module.GetArtifact(fixture.Path, artifact.ArtifactId);

        Assert.True(content.Data!.Truncated);
        Assert.Equal(256 * 1024, content.Data.Text!.Length);
    }

    [Fact]
    public void Workflow_Status_Uses_The_Latest_Existing_Release_Audit()
    {
        using var fixture = CreateProject();
        var path = Path.Combine(fixture.Path, ".pcbhelper", "release-audits", "latest", "release-audit.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            """
            {
              "disposition": "PROTOTYPE-ONLY",
              "checks": [
                { "status": "WARN", "summary": "Review orientation" },
                { "status": "FAIL", "summary": "Missing evidence" }
              ]
            }
            """);

        var status = CreateModule().GetStatus(fixture.Path);

        Assert.True(status.Success, status.Error?.Message);
        Assert.Equal("PROTOTYPE-ONLY", status.Data!.ReleaseDisposition);
        Assert.Equal(WorkflowPhase.Planning, status.Data.Phase);
        Assert.Equal("Review orientation", Assert.Single(status.Data.Warnings));
        Assert.Equal("Missing evidence", Assert.Single(status.Data.Blockers));
        Assert.NotNull(status.Data.LatestReleaseAuditArtifactId);
    }

    [Fact]
    public void Workflow_Status_Reports_Corrupt_Release_Audit_Instead_Of_Ignoring_It()
    {
        using var fixture = CreateProject();
        var path = Path.Combine(fixture.Path, ".pcbhelper", "release-audits", "latest", "release-audit.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{not-json");

        var status = CreateModule().GetStatus(fixture.Path);

        Assert.False(status.Success);
        Assert.Equal("RELEASE_AUDIT_ARTIFACT_INVALID", status.Error?.Code);
    }

    private static WorkflowArtifactService CreateModule()
    {
        var projects = new ProjectDiscoveryService();
        return new WorkflowArtifactService(projects, new ProjectTransactionStore(projects));
    }

    private static TempDirectory CreateProject()
    {
        var fixture = new TempDirectory();
        File.WriteAllText(Path.Combine(fixture.Path, "artifact.kicad_pro"), "{}");
        File.WriteAllText(Path.Combine(fixture.Path, "artifact.kicad_pcb"), "(kicad_pcb)");
        return fixture;
    }
}
