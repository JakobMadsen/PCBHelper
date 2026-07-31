using System.Diagnostics;
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
    public void Audit_Uses_ZeroFinding_KiCad_Drc_As_ZoneAware_Connectivity_Evidence()
    {
        using var temp = new TempDirectory();
        foreach (var file in Directory.GetFiles(Fixture))
        {
            File.Copy(file, Path.Combine(temp.Path, Path.GetFileName(file)));
        }

        var projects = new ProjectDiscoveryService();
        var finishing = new BoardFinishingService(projects);
        Assert.True(finishing.AddTestPoint(temp.Path, "TP_ZONE_ONLY", "GND", 60, 60, 2, dryRun: false).Success);
        Assert.NotEmpty(new RoutingService(projects).ListUnroutedConnections(temp.Path).Data!.Nets);

        var releaseDirectory = Path.Combine(temp.Path, ".pcbhelper", "releases", "current");
        Directory.CreateDirectory(releaseDirectory);
        File.WriteAllText(
            Path.Combine(releaseDirectory, "release-review.json"),
            """
            {
              "engineeringGate": {
                "checks": [
                  {
                    "kind": "drc",
                    "required": true,
                    "status": "Passed",
                    "findingCount": 0
                  }
                ]
              }
            }
            """);
        var policy = WritePolicy(temp.Path, """
            {
              "version": 1,
              "releaseEvidence": {
                "required": true,
                "requiredChecks": ["drc"]
              }
            }
            """);

        var result = CreateService().Audit(temp.Path, policy, Path.Combine(temp.Path, "audit"));

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(ReleaseAuditDispositions.Ready, result.Data!.Disposition);
        var check = Assert.Single(result.Data.Checks, item => item.Id == "unrouted-connections");
        Assert.Equal(ReleaseAuditCheckStatus.Pass, check.Status);
        Assert.Contains("zone-aware", check.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Audit_Recognizes_A_Clean_Committed_Git_Worktree()
    {
        using var fixture = CopyFixture(Fixture);
        RunGit(fixture.Path, "init", "-b", "main");
        RunGit(fixture.Path, "config", "user.name", "PCBHelper Test");
        RunGit(fixture.Path, "config", "user.email", "pcbhelper-test@local.invalid");
        RunGit(fixture.Path, "add", ".");
        RunGit(fixture.Path, "commit", "-m", "fixture");
        var policy = WritePolicy(fixture.Path, """
            {
              "version": 1,
              "git": {
                "required": true,
                "requireClean": true
              }
            }
            """);
        RunGit(fixture.Path, "add", "release-policy.json");
        RunGit(fixture.Path, "commit", "-m", "release policy");

        var result = CreateService().Audit(
            fixture.Path,
            policy,
            Path.Combine(fixture.Path, "audit"));

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(result.Data);
        Assert.Equal(ReleaseAuditDispositions.Ready, result.Data.Disposition);
        Assert.DoesNotContain(result.Data.Checks, check =>
            check.Id == "git-worktree" && check.Status == ReleaseAuditCheckStatus.Fail);
        Assert.Contains(result.Data.Checks, check =>
            check.Id == "git-head" && check.Status == ReleaseAuditCheckStatus.Pass);
        Assert.Contains(result.Data.Checks, check =>
            check.Id == "git-clean" && check.Status == ReleaseAuditCheckStatus.Pass);
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

    [Fact]
    public void Audit_Blocks_When_Required_Best_Practice_Review_Does_Not_Pass()
    {
        using var fixture = CopyFixture(Fixture);
        var policy = WritePolicy(fixture.Path, """
            {
              "version": 1,
              "bestPracticeReview": {
                "required": true,
                "requireFresh": true,
                "allowPassWithConcerns": false
              }
            }
            """);
        var bestPractices = CreateBestPracticeService(new ProjectDiscoveryService());
        var prepared = bestPractices.Prepare(fixture.Path);
        Assert.True(prepared.Success, prepared.Error?.Message);
        var assessment = JsonSerializer.Serialize(new
        {
            version = 1,
            promptVersion = prepared.Data!.PromptVersion,
            reviewId = prepared.Data.ReviewId,
            evidenceHash = prepared.Data.EvidenceHash,
            reviewer = new { kind = "llm", model = "release-audit-test" },
            criteria = BestPracticeRuleCatalog.All.Select(rule => new
            {
                ruleId = rule.Id,
                status = rule.Id == "PWR-RETURN-001" ? "fail" : "unableToAssess",
                confidence = rule.Id == "PWR-RETURN-001" ? "high" : "low",
                summary = rule.Id == "PWR-RETURN-001"
                    ? "A demonstrated review failure blocks release."
                    : "The fixture does not contain enough reviewed evidence.",
                evidenceIds = rule.Id == "PWR-RETURN-001" ? new[] { "board.tracks" } : Array.Empty<string>(),
                recommendation = rule.Id == "PWR-RETURN-001"
                    ? "Resolve the finding before release."
                    : null,
                applicabilityRationale = (string?)null
            }),
            unresolvedQuestions = Array.Empty<string>(),
            limitations = Array.Empty<string>()
        });
        var submitted = bestPractices.Submit(fixture.Path, assessment, prepared.Data.EvidenceHash);
        Assert.True(submitted.Success, submitted.Error?.Message);
        Assert.Equal(BestPracticeDisposition.Revise, submitted.Data!.Disposition);

        var result = CreateService(bestPractices).Audit(
            fixture.Path,
            policy,
            Path.Combine(fixture.Path, "audit"));

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(ReleaseAuditDispositions.Blocked, result.Data!.Disposition);
        var check = Assert.Single(result.Data.Checks, item => item.Id == "best-practice-review");
        Assert.Equal(ReleaseAuditCheckStatus.Fail, check.Status);
        Assert.Contains("Revise", check.Summary, StringComparison.Ordinal);
    }

    private static ReleaseAuditService CreateService(BestPracticeReviewService? bestPractices = null)
    {
        var projects = new ProjectDiscoveryService();
        bestPractices ??= CreateBestPracticeService(projects);
        return new ReleaseAuditService(
            projects,
            new ComponentService(projects),
            new BoardInspectionService(projects),
            new RoutingService(projects),
            new TestSpecService(projects),
            bestPractices,
            () => new SimulationCapabilities(true, "fake", "fake", "test", null),
            () => new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero));
    }

    private static BestPracticeReviewService CreateBestPracticeService(ProjectDiscoveryService projects)
    {
        return new BestPracticeReviewService(
            projects,
            new BoardSummaryService(projects),
            new BoardInspectionService(projects),
            new ComponentService(projects),
            new RoutingService(projects),
            new SchematicAuthoringService(projects),
            new TestSpecService(projects),
            () => new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero));
    }

    private static string WritePolicy(string directory, string content)
    {
        var path = Path.Combine(directory, "release-policy.json");
        File.WriteAllText(path, content);
        return path;
    }

    private static TempDirectory CopyFixture(string source)
    {
        var result = new TempDirectory();
        CopyDirectory(source, result.Path);
        return result;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start);
        Assert.NotNull(process);
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed ({process.ExitCode}).\n{stdout}\n{stderr}");
    }
}
