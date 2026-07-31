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
    public void Audit_Preserves_Git_Stderr_When_Worktree_Inspection_Fails()
    {
        using var fixture = CopyFixture(Fixture);
        var policy = WritePolicy(fixture.Path, """
        { "version": 1, "git": { "required": true } }
        """);

        var result = CreateService().Audit(fixture.Path, policy, Path.Combine(fixture.Path, "audit"));

        Assert.True(result.Success, result.Error?.Message);
        var check = Assert.Single(result.Data!.Checks, item => item.Id == "git-worktree");
        Assert.Equal(ReleaseAuditCheckStatus.Fail, check.Status);
        var evidence = JsonSerializer.Serialize(check.Evidence);
        Assert.Contains("standardError", evidence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exitCode", evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Audit_Uses_Process_Local_Safe_Directory_Without_Global_Config_Mutation()
    {
        using var fixture = CopyFixture(Fixture);
        RunGit(fixture.Path, "init", "-b", "main");
        RunGit(fixture.Path, "config", "user.name", "PCBHelper Test");
        RunGit(fixture.Path, "config", "user.email", "pcbhelper-test@local.invalid");
        var policy = WritePolicy(fixture.Path, """
            { "version": 1, "git": { "required": true, "requireClean": true } }
            """);
        RunGit(fixture.Path, "add", ".");
        RunGit(fixture.Path, "commit", "-m", "fixture and policy");
        var isolatedGlobalConfig = Path.Combine(fixture.Path, "isolated-global.gitconfig");
        var previousGlobal = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        var previousOwner = Environment.GetEnvironmentVariable("GIT_TEST_ASSUME_DIFFERENT_OWNER");
        try
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", isolatedGlobalConfig);
            Environment.SetEnvironmentVariable("GIT_TEST_ASSUME_DIFFERENT_OWNER", "1");

            var result = CreateService().Audit(fixture.Path, policy, Path.Combine(fixture.Path, "audit"));

            Assert.True(result.Success, result.Error?.Message);
            Assert.Equal(ReleaseAuditCheckStatus.Pass, Assert.Single(result.Data!.Checks, item => item.Id == "git-head").Status);
            Assert.Equal(ReleaseAuditCheckStatus.Pass, Assert.Single(result.Data.Checks, item => item.Id == "git-clean").Status);
            Assert.False(File.Exists(isolatedGlobalConfig));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", previousGlobal);
            Environment.SetEnvironmentVariable("GIT_TEST_ASSUME_DIFFERENT_OWNER", previousOwner);
        }
    }

    [Fact]
    public void Audit_Disposes_Assembly_Diagnostics_Without_Hiding_Orientation_Warnings()
    {
        using var fixture = CopyFixture(Fixture);
        var releaseDirectory = Path.Combine(fixture.Path, ".pcbhelper", "releases", "current");
        Directory.CreateDirectory(releaseDirectory);
        File.WriteAllText(Path.Combine(releaseDirectory, "release-review.json"), """
        {
          "engineeringGate": {
            "checks": [{
              "kind": "manufacturing-validation",
              "required": true,
              "status": "Passed",
              "findingCount": 3
            }]
          },
          "assembly": {
            "diagnostics": [
              { "severity": "warning", "code": "ASSEMBLY_BOARD_ONLY_TESTPOINT", "reference": "TP1", "message": "Board-only testpoint." },
              { "severity": "warning", "code": "ASSEMBLY_THT_CPL_EXCLUDED", "reference": "J1", "message": "Expected THT exclusion." },
              { "severity": "warning", "code": "ASSEMBLY_ORIENTATION_REVIEW", "reference": "U1", "message": "Pin-1 review required." }
            ]
          }
        }
        """);
        var policy = WritePolicy(fixture.Path, """
        {
          "version": 1,
          "releaseEvidence": {
            "required": true,
            "requiredChecks": ["manufacturing-validation"],
            "diagnosticDispositions": [
              { "code": "ASSEMBLY_BOARD_ONLY_TESTPOINT", "subjects": ["TP1"], "disposition": "info" },
              { "code": "ASSEMBLY_THT_CPL_EXCLUDED", "subjects": ["J1"], "disposition": "info" }
            ],
            "manualAcceptanceAllowedCodes": ["ASSEMBLY_ORIENTATION_REVIEW"]
          }
        }
        """);

        var result = CreateService().Audit(fixture.Path, policy, Path.Combine(fixture.Path, "audit"));

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(ReleaseAuditDispositions.PrototypeOnly, result.Data!.Disposition);
        var check = Assert.Single(result.Data.Checks, item => item.Id == "assembly-diagnostic-dispositions");
        Assert.Equal(ReleaseAuditCheckStatus.Warn, check.Status);
        var evidence = JsonSerializer.Serialize(check.Evidence);
        Assert.Contains("U1", evidence, StringComparison.Ordinal);
        Assert.Contains("ASSEMBLY_ORIENTATION_REVIEW", evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_Accepts_Only_Hash_Current_Reference_Specific_Diagnostic_Signoff()
    {
        using var fixture = CopyFixture(Fixture);
        var releaseDirectory = Path.Combine(fixture.Path, ".pcbhelper", "releases", "current");
        Directory.CreateDirectory(releaseDirectory);
        File.WriteAllText(Path.Combine(releaseDirectory, "release-review.json"), """
        {
          "engineeringGate": { "checks": [{ "kind": "manufacturing-validation", "required": true, "status": "Passed", "findingCount": 1 }] },
          "assembly": { "diagnostics": [{ "severity": "warning", "code": "ASSEMBLY_ORIENTATION_REVIEW", "reference": "U1", "message": "Review U1." }] }
        }
        """);
        var policy = WritePolicy(fixture.Path, """
        {
          "version": 1,
          "releaseEvidence": {
            "required": true,
            "requiredChecks": ["manufacturing-validation"],
            "manualAcceptanceAllowedCodes": ["ASSEMBLY_ORIENTATION_REVIEW"]
          }
        }
        """);
        var service = CreateService();
        var first = service.Audit(fixture.Path, policy, Path.Combine(fixture.Path, "audit-first"));
        var disposition = Assert.Single(first.Data!.Checks, item => item.Id == "assembly-diagnostic-dispositions");
        using var evidence = JsonDocument.Parse(JsonSerializer.Serialize(disposition.Evidence));
        var designHash = evidence.RootElement.GetProperty("currentDesignHash").GetString();
        Directory.CreateDirectory(Path.Combine(fixture.Path, ".pcbhelper"));
        File.WriteAllText(Path.Combine(fixture.Path, ".pcbhelper", "release-signoff.json"), $$"""
        {
          "acceptedDiagnostics": [{
            "code": "ASSEMBLY_ORIENTATION_REVIEW",
            "subject": "U1",
            "designHash": "{{designHash}}",
            "reviewer": "fixture-reviewer",
            "reviewedAtUtc": "2026-07-30T10:00:00Z",
            "rationale": "Pin 1 was checked against the assembly drawing."
          }]
        }
        """);

        var accepted = service.Audit(fixture.Path, policy, Path.Combine(fixture.Path, "audit-accepted"));

        Assert.Equal(ReleaseAuditDispositions.Ready, accepted.Data!.Disposition);
        Assert.Equal(
            ReleaseAuditCheckStatus.Pass,
            Assert.Single(accepted.Data.Checks, item => item.Id == "assembly-diagnostic-dispositions").Status);

        var boardPath = Directory.GetFiles(fixture.Path, "*.kicad_pcb").Single();
        File.AppendAllText(boardPath, Environment.NewLine);
        var stale = service.Audit(fixture.Path, policy, Path.Combine(fixture.Path, "audit-stale"));

        Assert.Equal(ReleaseAuditDispositions.PrototypeOnly, stale.Data!.Disposition);
        Assert.Equal(
            ReleaseAuditCheckStatus.Warn,
            Assert.Single(stale.Data.Checks, item => item.Id == "assembly-diagnostic-dispositions").Status);
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
    public void Audit_Applies_Advisory_And_Blocking_Board_Readability_Policy()
    {
        using var fixture = CopyFixture(Path.Combine(RepoRoot.Path, "fixtures", "blank-authoring"));
        var boardPath = Directory.GetFiles(fixture.Path, "*.kicad_pcb").Single();
        var board = File.ReadAllText(boardPath);
        File.WriteAllText(boardPath, board.Insert(board.LastIndexOf(')'), """

          (footprint "PCBHelper:TestPoint" (layer "F.Cu") (at 20 20)
            (property "Reference" "TP1" (at 0 -2 0) (layer "F.SilkS") (hide yes) (effects (font (size 1 1))))
            (property "Value" "TestPoint" (at 0 2 0) (layer "F.Fab") (hide yes) (effects (font (size 1 1))))
            (pad "1" thru_hole circle (at 0 0) (size 2 2) (drill 1) (layers "*.Cu" "*.Mask") (net 1 "GND")))
        """));
        var advisoryPolicy = WritePolicy(fixture.Path, """
            { "version": 1, "boardReadability": { "required": true } }
            """);

        var advisory = CreateService().Audit(fixture.Path, advisoryPolicy, Path.Combine(fixture.Path, "audit-advisory"));

        Assert.True(advisory.Success, advisory.Error?.Message);
        Assert.Equal(ReleaseAuditDispositions.PrototypeOnly, advisory.Data!.Disposition);
        Assert.Equal(ReleaseAuditCheckStatus.Warn, Assert.Single(advisory.Data.Checks, item => item.Id == "board-readability").Status);

        var blockingPolicy = WritePolicy(fixture.Path, """
            {
              "version": 1,
              "boardReadability": {
                "required": true,
                "blockingDiagnosticCodes": ["BOARD_TP_LABEL_MISSING"]
              }
            }
            """);
        var blocked = CreateService().Audit(fixture.Path, blockingPolicy, Path.Combine(fixture.Path, "audit-blocked"));

        Assert.True(blocked.Success, blocked.Error?.Message);
        Assert.Equal(ReleaseAuditDispositions.Blocked, blocked.Data!.Disposition);
        Assert.Equal(ReleaseAuditCheckStatus.Fail, Assert.Single(blocked.Data.Checks, item => item.Id == "board-readability").Status);
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
            new BoardReadabilityService(projects),
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
