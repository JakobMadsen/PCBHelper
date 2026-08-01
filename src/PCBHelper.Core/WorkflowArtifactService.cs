using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCBHelper.Core;

public sealed class WorkflowArtifactService
{
    private const int MaximumInlineArtifactBytes = 256 * 1024;
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".md", ".txt", ".csv", ".log"
    };

    private readonly ProjectDiscoveryService _projects;
    private readonly ProjectTransactionStore _transactions;

    public WorkflowArtifactService(ProjectDiscoveryService projects, ProjectTransactionStore transactions)
    {
        _projects = projects;
        _transactions = transactions;
    }

    public ToolResponse<WorkflowStatus> GetStatus(string projectPath)
    {
        var project = _projects.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
            return ToolResponse<WorkflowStatus>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);

        var latest = _transactions.List(project.Data.ProjectRoot).Data?.Transactions.OrderByDescending(static item => item.CreatedAtUtc).FirstOrDefault();
        var artifacts = ListArtifacts(projectPath);
        if (!artifacts.Success || artifacts.Data is null)
            return ToolResponse<WorkflowStatus>.Fail(artifacts.Summary, artifacts.Error?.Code ?? "ARTIFACT_LIST_FAILED", artifacts.Error?.Message);

        var latestAudit = artifacts.Data.Artifacts
            .Where(static item => item.RelativePath.EndsWith("/release-audit.json", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static item => item.LastWriteUtc)
            .FirstOrDefault();
        var disposition = "UNKNOWN";
        var blockers = new List<string>();
        var warnings = new List<string>();
        if (latestAudit is not null)
        {
            var content = GetArtifact(projectPath, latestAudit.ArtifactId);
            if (!content.Success || content.Data?.Text is null)
                return ToolResponse<WorkflowStatus>.Fail("Latest release audit could not be read.", content.Error?.Code ?? "ARTIFACT_READ_FAILED", content.Error?.Message);
            try
            {
                using var document = JsonDocument.Parse(content.Data.Text);
                var root = document.RootElement;
                if (root.TryGetProperty("disposition", out var dispositionElement)) disposition = dispositionElement.GetString() ?? "UNKNOWN";
                if (root.TryGetProperty("checks", out var checks) && checks.ValueKind == JsonValueKind.Array)
                {
                    foreach (var check in checks.EnumerateArray())
                    {
                        var status = check.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
                        var summary = check.TryGetProperty("summary", out var summaryElement) ? summaryElement.GetString() : null;
                        if (string.IsNullOrWhiteSpace(summary)) continue;
                        if (string.Equals(status, "FAIL", StringComparison.OrdinalIgnoreCase)) blockers.Add(summary);
                        else if (string.Equals(status, "WARN", StringComparison.OrdinalIgnoreCase)) warnings.Add(summary);
                    }
                }
            }
            catch (JsonException exception)
            {
                return ToolResponse<WorkflowStatus>.Fail("Latest release audit is invalid JSON.", "RELEASE_AUDIT_ARTIFACT_INVALID", exception.Message);
            }
        }

        var sourceFiles = new[] { project.Data.ProjectFile, project.Data.SchematicFile, project.Data.BoardFile }
            .Where(static item => item is not null).Cast<string>().ToArray();
        var fingerprint = HashFiles(project.Data.ProjectRoot, sourceFiles);
        var phase = latest is null ? WorkflowPhase.Planning
            : latest.Status is ProjectTransactionStatus.Prepared or ProjectTransactionStatus.Applying or ProjectTransactionStatus.Applied ? WorkflowPhase.Design
            : string.Equals(disposition, ReleaseAuditDispositions.Ready, StringComparison.OrdinalIgnoreCase) ? WorkflowPhase.Release
            : WorkflowPhase.Verification;
        var statusResult = new WorkflowStatus(
            phase,
            disposition,
            fingerprint,
            latest?.TransactionId,
            latest?.Status,
            latestAudit?.ArtifactId,
            blockers,
            warnings,
            artifacts.Data.Artifacts.Count);
        return ToolResponse<WorkflowStatus>.Ok($"Workflow is in {phase.ToString().ToLowerInvariant()} with disposition {disposition}.", statusResult);
    }

    public ToolResponse<ArtifactListResult> ListArtifacts(string projectPath)
    {
        var project = _projects.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
            return ToolResponse<ArtifactListResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        var artifactRoot = Path.Combine(project.Data.ProjectRoot, ".pcbhelper");
        if (!Directory.Exists(artifactRoot))
            return ToolResponse<ArtifactListResult>.Ok("No PCBHelper artifacts exist.", new([]));

        try
        {
            var artifacts = EnumerateFilesWithoutReparsePoints(artifactRoot)
                .Where(path => !Path.GetRelativePath(artifactRoot, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => string.Equals(segment, "locks", StringComparison.OrdinalIgnoreCase)))
                .Select(path => Describe(project.Data.ProjectRoot, path))
                .OrderByDescending(static item => item.LastWriteUtc)
                .ToArray();
            return ToolResponse<ArtifactListResult>.Ok($"Found {artifacts.Length} artifact(s).", new(artifacts));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ToolResponse<ArtifactListResult>.Fail("PCBHelper artifacts could not be enumerated.", "ARTIFACT_LIST_FAILED", exception.Message);
        }
    }

    public ToolResponse<ArtifactContent> GetArtifact(string projectPath, string artifactId)
    {
        var listed = ListArtifacts(projectPath);
        if (!listed.Success || listed.Data is null)
            return ToolResponse<ArtifactContent>.Fail(listed.Summary, listed.Error?.Code ?? "ARTIFACT_LIST_FAILED", listed.Error?.Message);
        var artifact = listed.Data.Artifacts.SingleOrDefault(item => string.Equals(item.ArtifactId, artifactId, StringComparison.Ordinal));
        if (artifact is null) return ToolResponse<ArtifactContent>.Fail("Artifact was not found.", "ARTIFACT_NOT_FOUND");
        var project = _projects.GetSummary(projectPath).Data!;
        var path = Path.GetFullPath(artifact.RelativePath, project.ProjectRoot);
        if (!ProjectScopePolicy.IsWithin(Path.Combine(project.ProjectRoot, ".pcbhelper"), path) || HasReparsePoint(path, project.ProjectRoot))
            return ToolResponse<ArtifactContent>.Fail("Artifact path escaped the project artifact directory.", "PROJECT_SCOPE_VIOLATION");
        if (!File.Exists(path)) return ToolResponse<ArtifactContent>.Fail("Artifact changed after listing.", "ARTIFACT_CHANGED");
        var currentHash = HashFile(path);
        if (!string.Equals(currentHash, artifact.Sha256, StringComparison.Ordinal))
            return ToolResponse<ArtifactContent>.Fail("Artifact changed after listing.", "ARTIFACT_CHANGED");
        if (!TextExtensions.Contains(Path.GetExtension(path)))
            return ToolResponse<ArtifactContent>.Ok("Artifact metadata loaded; binary content is not returned inline.", new(artifact, null, false));
        try
        {
            var bytes = File.ReadAllBytes(path);
            var truncated = bytes.Length > MaximumInlineArtifactBytes;
            var text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, MaximumInlineArtifactBytes));
            return ToolResponse<ArtifactContent>.Ok(truncated ? "Artifact content was truncated." : "Artifact content loaded.", new(artifact, text, truncated));
        }
        catch (IOException exception)
        {
            return ToolResponse<ArtifactContent>.Fail("Artifact could not be read.", "ARTIFACT_READ_FAILED", exception.Message);
        }
    }

    private static IEnumerable<string> EnumerateFilesWithoutReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var file in Directory.EnumerateFiles(directory))
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) yield return file;
            foreach (var child in Directory.EnumerateDirectories(directory)) pending.Push(child);
        }
    }

    private static bool HasReparsePoint(string path, string projectRoot)
    {
        for (var current = new FileInfo(path).Directory; current is not null && ProjectScopePolicy.IsWithin(projectRoot, current.FullName); current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static ArtifactDescriptor Describe(string projectRoot, string path)
    {
        var relative = Path.GetRelativePath(projectRoot, path).Replace('\\', '/');
        var sha256 = HashFile(path);
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(relative + "\n" + sha256)))[..24];
        var info = new FileInfo(path);
        var kind = relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() ?? "artifact";
        return new ArtifactDescriptor(id, kind, relative, info.Length, info.LastWriteTimeUtc, sha256);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string HashFiles(string projectRoot, IReadOnlyList<string> paths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths.OrderBy(path => Path.GetRelativePath(projectRoot, path), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(projectRoot, path).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            using var stream = File.OpenRead(path);
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer.AsSpan(0, read));
            hash.AppendData([0]);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

public enum WorkflowPhase { Planning, Design, Verification, Release }
public sealed record WorkflowStatus(WorkflowPhase Phase, string ReleaseDisposition, string ProjectFingerprint,
    string? LatestTransactionId, ProjectTransactionStatus? LatestTransactionStatus, string? LatestReleaseAuditArtifactId,
    IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings, int ArtifactCount);
public sealed record ArtifactDescriptor(string ArtifactId, string Kind, string RelativePath, long SizeBytes, DateTimeOffset LastWriteUtc, string Sha256);
public sealed record ArtifactListResult(IReadOnlyList<ArtifactDescriptor> Artifacts);
public sealed record ArtifactContent(ArtifactDescriptor Artifact, string? Text, bool Truncated);
