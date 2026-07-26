using System.Text.Json;
using PCBHelper.Core;

namespace PCBHelper.Core.Tests;

public sealed class ReleaseAuditServiceTests
{
    private static readonly string Fixture =
        Path.Combine(RepoRoot.Path, "fixtures", "kicad-getting-started-led");

    [Fact]
    public void Audit_Writes_Ready_Json_And_Markdown_Reports()
    {
        using var temp = new TempDirectory();
        var policy = WritePolicy(temp.Path, """
            {
              "version": 1,
              "name": "fixture release"
            }
            """);
        var service = CreateService();

        var result = service.Audit(Fixture, policy, temp.Path);

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(result.Data);
        Assert.Equal(ReleaseAuditDispositions.Ready, result.Data.Disposition);
        Assert.Equal(3, result.Data.Summary.Pass);
        Assert.Equal(0, result.Data.Summary.Fail);
        Assert.True(File.Exists(result.Data.JsonReportPath));
        Assert.True(File.Exists(result.Data.MarkdownReportPath));
        Assert.Contains("Disposition: READY", File.ReadAllText(result.Data.MarkdownReportPath));
        using var report = JsonDocument.Parse(File.ReadAllText(result.Data.JsonReportPath));
        Assert.Equal("READY", report.RootElement.GetProperty("disposition").GetString());
        Assert.Equal(
            "PASS",
            report.RootElement.GetProperty("checks")[0].GetProperty("status").GetString());
    }

    [Fact]
    public void Audit_Blocks_On_Pin_Net_And_Dc_Path_Failures()
    {
        using var temp = new TempDirectory();
        var policy = WritePolicy(temp.Path, """
            {
              "version": 1,
              "pinNetAssertions": [
                { "id": "wrong-led-return", "reference": "R1", "pad": "2", "net": "GND" }
              ],
              "dcFeedbackAssertions": [
                { "id": "missing-resistive-path", "fromNet": "VCC", "toNet": "GND" }
              ]
            }
            """);
        var service = CreateService();

        var result = service.Audit(Fixture, policy, temp.Path);

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(result.Data);
        Assert.Equal(ReleaseAuditDispositions.Blocked, result.Data.Disposition);
        Assert.Contains(result.Data.Checks, check =>
            check.Id == "wrong-led-return" && check.Status == ReleaseAuditCheckStatus.Fail);
        Assert.Contains(result.Data.Checks, check =>
            check.Id == "missing-resistive-path" && check.Status == ReleaseAuditCheckStatus.Fail);
    }

    [Fact]
    public void Audit_Accepts_Array_Mirror_Pairs_And_Finds_Resistive_Path()
    {
        using var temp = new TempDirectory();
        var policy = WritePolicy(temp.Path, """
            {
              "version": 1,
              "mirroredValuePairs": [
                ["R1", "R1"]
              ],
              "dcFeedbackAssertions": [
                { "id": "series-resistor-path", "fromNet": "VCC", "toNet": "LED_A" }
              ]
            }
            """);
        var service = CreateService();

        var result = service.Audit(Fixture, policy, temp.Path);

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(result.Data);
        Assert.Equal(ReleaseAuditDispositions.Ready, result.Data.Disposition);
        Assert.Contains(result.Data.Checks, check =>
            check.Id == "mirror-R1-R1" && check.Status == ReleaseAuditCheckStatus.Pass);
        Assert.Contains(result.Data.Checks, check =>
            check.Id == "series-resistor-path" && check.Status == ReleaseAuditCheckStatus.Pass);
    }

    [Fact]
    public void Audit_Rejects_Unsupported_Policy_Version()
    {
        using var temp = new TempDirectory();
        var policy = WritePolicy(temp.Path, """{ "version": 2 }""");

        var result = CreateService().Audit(Fixture, policy, temp.Path);

        Assert.False(result.Success);
        Assert.Equal("RELEASE_AUDIT_POLICY_INVALID", result.Error?.Code);
    }

    private static ReleaseAuditService CreateService()
    {
        var projects = new ProjectDiscoveryService();
        return new ReleaseAuditService(
            projects,
            new ComponentService(projects),
            new BoardInspectionService(projects),
            new RoutingService(projects),
            new TestSpecService(projects),
            () => new SimulationCapabilities(true, "fake", "fake", "test", null),
            () => new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero));
    }

    private static string WritePolicy(string directory, string content)
    {
        var path = Path.Combine(directory, "release-policy.json");
        File.WriteAllText(path, content);
        return path;
    }
}
