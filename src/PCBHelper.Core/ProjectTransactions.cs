using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCBHelper.Core;

public sealed class ProjectTransactionService
{
    private readonly ProjectDiscoveryService _projectDiscovery;
    private readonly ProjectTransactionStore _store;
    private readonly IProjectFileWriter _fileWriter;
    private readonly Func<DateTimeOffset> _utcNow;

    public ProjectTransactionService(ProjectDiscoveryService projectDiscovery)
        : this(projectDiscovery, new ProjectTransactionStore(projectDiscovery), new AtomicProjectFileWriter(), () => DateTimeOffset.UtcNow)
    {
    }

    public ProjectTransactionService(
        ProjectDiscoveryService projectDiscovery,
        ProjectTransactionStore store,
        IProjectFileWriter fileWriter,
        Func<DateTimeOffset> utcNow)
    {
        _projectDiscovery = projectDiscovery;
        _store = store;
        _fileWriter = fileWriter;
        _utcNow = utcNow;
    }

    public async Task<ToolResponse<ProjectTransactionResult>> ApplyAsync(
        string projectPath,
        string goal,
        string planHash,
        IReadOnlyList<PreparedOperation> operations,
        IReadOnlyList<PreparedFileChange> changes,
        IReadOnlyList<string>? acknowledgedDecisionIds = null,
        CancellationToken cancellationToken = default)
    {
        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<ProjectTransactionResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        if (changes.Count == 0)
        {
            return ToolResponse<ProjectTransactionResult>.Fail("The prepared plan contains no file changes.", "TRANSACTION_EMPTY");
        }

        var incomplete = _store.List(project.Data.ProjectRoot).Data?.Transactions
            .FirstOrDefault(item => item.Status == ProjectTransactionStatus.Incomplete);
        if (incomplete is not null)
        {
            return ToolResponse<ProjectTransactionResult>.Fail(
                $"Project has an incomplete transaction that must be resolved first: {incomplete.TransactionId}",
                "TRANSACTION_INCOMPLETE");
        }

        var normalized = NormalizeChanges(project.Data.ProjectRoot, changes);
        if (!normalized.Success || normalized.Data is null)
        {
            return ToolResponse<ProjectTransactionResult>.Fail(normalized.Summary, normalized.Error?.Code ?? "PROJECT_SCOPE_VIOLATION", normalized.Error?.Message);
        }

        var now = _utcNow();
        var id = $"{now.UtcDateTime.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}";
        var record = new ProjectTransactionRecord(
            id,
            goal,
            planHash,
            ProjectTransactionStatus.Prepared,
            now,
            now,
            operations.Select(static operation => new TransactionOperationRecord(operation.Id, operation.Type, operation.Summary)).ToArray(),
            normalized.Data.Select(static change => new TransactionFileRecord(change.RelativePath, change.BeforeHash, change.AfterHash)).ToArray(),
            acknowledgedDecisionIds ?? Array.Empty<string>(),
            null,
            null);

        var transactionRoot = _store.GetTransactionRoot(project.Data.ProjectRoot, id);
        Directory.CreateDirectory(transactionRoot);
        await _store.WriteSnapshotsAsync(project.Data.ProjectRoot, id, normalized.Data, cancellationToken);
        await _store.WriteAsync(project.Data.ProjectRoot, record, cancellationToken);

        await using var projectLock = TryAcquireProjectLock(project.Data.ProjectRoot);
        if (projectLock is null)
        {
            return ToolResponse<ProjectTransactionResult>.Fail("Another PCBHelper transaction is active for this project.", "PROJECT_LOCKED");
        }

        var written = new List<PreparedFileChange>();
        try
        {
            foreach (var change in normalized.Data)
            {
                var target = Path.Combine(project.Data.ProjectRoot, change.RelativePath);
                var current = File.Exists(target) ? await File.ReadAllTextAsync(target, cancellationToken) : null;
                if (!string.Equals(ContentHash(current), change.BeforeHash, StringComparison.Ordinal))
                {
                    return ToolResponse<ProjectTransactionResult>.Fail(
                        $"Project file changed after preview: {change.RelativePath}",
                        "TRANSACTION_CONFLICT");
                }
            }

            record = record with { Status = ProjectTransactionStatus.Applying, UpdatedAtUtc = _utcNow() };
            await _store.WriteAsync(project.Data.ProjectRoot, record, cancellationToken);
            foreach (var change in normalized.Data)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WriteContentAsync(project.Data.ProjectRoot, change.RelativePath, change.AfterContent, cancellationToken);
                written.Add(change);
            }

            record = record with { Status = ProjectTransactionStatus.Applied, UpdatedAtUtc = _utcNow() };
            await _store.WriteAsync(project.Data.ProjectRoot, record, cancellationToken);
            return ToolResponse<ProjectTransactionResult>.Ok(
                $"Applied transaction {id}.",
                new ProjectTransactionResult(record, _store.GetManifestPath(project.Data.ProjectRoot, id)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            var rollbackSucceeded = await RollBackWrittenChangesAsync(project.Data.ProjectRoot, written);
            record = record with
            {
                Status = rollbackSucceeded ? ProjectTransactionStatus.RolledBack : ProjectTransactionStatus.Incomplete,
                UpdatedAtUtc = _utcNow(),
                Error = exception.Message
            };
            await _store.WriteAsync(project.Data.ProjectRoot, record, CancellationToken.None);
            return ToolResponse<ProjectTransactionResult>.Fail(
                rollbackSucceeded ? "Transaction failed and was rolled back." : "Transaction failed and rollback was incomplete.",
                exception is OperationCanceledException ? "TRANSACTION_CANCELLED" : rollbackSucceeded ? "TRANSACTION_APPLY_FAILED" : "TRANSACTION_INCOMPLETE",
                exception.Message,
                new ProjectTransactionResult(record, _store.GetManifestPath(project.Data.ProjectRoot, id)));
        }
    }

    public ToolResponse<ProjectTransactionResult> Get(string projectPath, string transactionId)
    {
        return _store.Read(projectPath, transactionId);
    }

    public async Task<ToolResponse<ProjectTransactionResult>> SetGateResultAsync(
        string projectPath,
        string transactionId,
        EngineeringGateResult gate,
        CancellationToken cancellationToken = default)
    {
        var read = _store.Read(projectPath, transactionId);
        if (!read.Success || read.Data is null)
        {
            return read;
        }

        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<ProjectTransactionResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        var record = read.Data.Transaction with
        {
            Status = gate.Status == EngineeringGateStatus.Passed ? ProjectTransactionStatus.GatePassed : ProjectTransactionStatus.GateFailed,
            Gate = gate,
            UpdatedAtUtc = _utcNow()
        };
        await _store.WriteAsync(project.Data.ProjectRoot, record, cancellationToken);
        return ToolResponse<ProjectTransactionResult>.Ok(
            $"Recorded engineering gate for transaction {transactionId}.",
            new ProjectTransactionResult(record, read.Data.ManifestPath));
    }

    public async Task<ToolResponse<ProjectTransactionResult>> RestoreAsync(
        string projectPath,
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        var read = _store.Read(projectPath, transactionId);
        if (!read.Success || read.Data is null)
        {
            return read;
        }

        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<ProjectTransactionResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        var record = read.Data.Transaction;
        if (record.Status is ProjectTransactionStatus.RolledBack or ProjectTransactionStatus.Prepared)
        {
            return ToolResponse<ProjectTransactionResult>.Fail($"Transaction {transactionId} is not in a restorable state.", "TRANSACTION_STATE_INVALID");
        }

        await using var projectLock = TryAcquireProjectLock(project.Data.ProjectRoot);
        if (projectLock is null)
        {
            return ToolResponse<ProjectTransactionResult>.Fail("Another PCBHelper transaction is active for this project.", "PROJECT_LOCKED");
        }

        var changes = await _store.ReadSnapshotsAsync(project.Data.ProjectRoot, record, cancellationToken);
        foreach (var change in changes)
        {
            var target = Path.Combine(project.Data.ProjectRoot, change.RelativePath);
            var current = File.Exists(target) ? await File.ReadAllTextAsync(target, cancellationToken) : null;
            if (!string.Equals(ContentHash(current), change.AfterHash, StringComparison.Ordinal))
            {
                return ToolResponse<ProjectTransactionResult>.Fail(
                    $"Cannot restore because a file changed after transaction {transactionId}: {change.RelativePath}",
                    "TRANSACTION_CONFLICT");
            }
        }

        var restored = new List<PreparedFileChange>();
        try
        {
            foreach (var change in changes.Reverse())
            {
                await WriteContentAsync(project.Data.ProjectRoot, change.RelativePath, change.BeforeContent, cancellationToken);
                restored.Add(change);
            }

            record = record with { Status = ProjectTransactionStatus.RolledBack, UpdatedAtUtc = _utcNow(), Error = null };
            await _store.WriteAsync(project.Data.ProjectRoot, record, cancellationToken);
            return ToolResponse<ProjectTransactionResult>.Ok(
                $"Restored transaction {transactionId}.",
                new ProjectTransactionResult(record, read.Data.ManifestPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            record = record with { Status = ProjectTransactionStatus.Incomplete, UpdatedAtUtc = _utcNow(), Error = exception.Message };
            await _store.WriteAsync(project.Data.ProjectRoot, record, CancellationToken.None);
            return ToolResponse<ProjectTransactionResult>.Fail(
                "Transaction restore was incomplete.",
                "TRANSACTION_INCOMPLETE",
                exception.Message,
                new ProjectTransactionResult(record, read.Data.ManifestPath));
        }
    }

    private static ToolResponse<IReadOnlyList<PreparedFileChange>> NormalizeChanges(string projectRoot, IReadOnlyList<PreparedFileChange> changes)
    {
        var normalized = new List<PreparedFileChange>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (Path.IsPathRooted(change.RelativePath))
            {
                return ToolResponse<IReadOnlyList<PreparedFileChange>>.Fail("Prepared changes must use project-relative paths.", "PROJECT_SCOPE_VIOLATION");
            }

            var target = Path.GetFullPath(change.RelativePath, projectRoot);
            if (!ProjectScopePolicy.IsWithin(projectRoot, target))
            {
                return ToolResponse<IReadOnlyList<PreparedFileChange>>.Fail($"Prepared change is outside the project: {change.RelativePath}", "PROJECT_SCOPE_VIOLATION");
            }

            var relative = Path.GetRelativePath(projectRoot, target);
            if (!seen.Add(relative))
            {
                return ToolResponse<IReadOnlyList<PreparedFileChange>>.Fail($"Prepared changes contain duplicate file: {relative}", "TRANSACTION_FILE_DUPLICATE");
            }

            normalized.Add(change with
            {
                RelativePath = relative,
                BeforeHash = ContentHash(change.BeforeContent),
                AfterHash = ContentHash(change.AfterContent)
            });
        }

        return ToolResponse<IReadOnlyList<PreparedFileChange>>.Ok("Validated prepared file changes.", normalized);
    }

    private async Task<bool> RollBackWrittenChangesAsync(string projectRoot, IReadOnlyList<PreparedFileChange> written)
    {
        try
        {
            foreach (var change in written.Reverse())
            {
                await WriteContentAsync(projectRoot, change.RelativePath, change.BeforeContent, CancellationToken.None);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task WriteContentAsync(string projectRoot, string relativePath, string? content, CancellationToken cancellationToken)
    {
        var target = Path.GetFullPath(relativePath, projectRoot);
        if (!ProjectScopePolicy.IsWithin(projectRoot, target))
        {
            throw new UnauthorizedAccessException($"Transaction target is outside the project: {relativePath}");
        }

        if (content is null)
        {
            if (File.Exists(target))
            {
                File.Delete(target);
            }

            return;
        }

        await _fileWriter.WriteAtomicAsync(target, content, cancellationToken);
    }

    private static FileStream? TryAcquireProjectLock(string projectRoot)
    {
        var lockRoot = Path.Combine(projectRoot, ".pcbhelper", "locks");
        Directory.CreateDirectory(lockRoot);
        try
        {
            return new FileStream(Path.Combine(lockRoot, "project.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static string ContentHash(string? content)
    {
        if (content is null)
        {
            return "missing";
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }
}

public sealed class ProjectTransactionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) }
    };

    private readonly ProjectDiscoveryService _projectDiscovery;

    public ProjectTransactionStore(ProjectDiscoveryService projectDiscovery)
    {
        _projectDiscovery = projectDiscovery;
    }

    public string GetTransactionRoot(string projectRoot, string transactionId)
    {
        return Path.Combine(projectRoot, ".pcbhelper", "transactions", ValidateId(transactionId));
    }

    public string GetManifestPath(string projectRoot, string transactionId)
    {
        return Path.Combine(GetTransactionRoot(projectRoot, transactionId), "transaction.json");
    }

    public async Task WriteAsync(string projectRoot, ProjectTransactionRecord record, CancellationToken cancellationToken)
    {
        var root = GetTransactionRoot(projectRoot, record.TransactionId);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(GetManifestPath(projectRoot, record.TransactionId), JsonSerializer.Serialize(record, JsonOptions), cancellationToken);
    }

    public async Task WriteSnapshotsAsync(string projectRoot, string transactionId, IReadOnlyList<PreparedFileChange> changes, CancellationToken cancellationToken)
    {
        var root = GetTransactionRoot(projectRoot, transactionId);
        foreach (var change in changes)
        {
            await WriteSnapshotAsync(Path.Combine(root, "before", change.RelativePath), change.BeforeContent, cancellationToken);
            await WriteSnapshotAsync(Path.Combine(root, "after", change.RelativePath), change.AfterContent, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<PreparedFileChange>> ReadSnapshotsAsync(string projectRoot, ProjectTransactionRecord record, CancellationToken cancellationToken)
    {
        var root = GetTransactionRoot(projectRoot, record.TransactionId);
        var changes = new List<PreparedFileChange>();
        foreach (var file in record.Files)
        {
            changes.Add(new PreparedFileChange(
                file.RelativePath,
                await ReadSnapshotAsync(Path.Combine(root, "before", file.RelativePath), cancellationToken),
                await ReadSnapshotAsync(Path.Combine(root, "after", file.RelativePath), cancellationToken),
                file.BeforeHash,
                file.AfterHash));
        }

        return changes;
    }

    public ToolResponse<ProjectTransactionResult> Read(string projectPath, string transactionId)
    {
        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<ProjectTransactionResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        string manifest;
        try
        {
            manifest = GetManifestPath(project.Data.ProjectRoot, transactionId);
        }
        catch (ArgumentException exception)
        {
            return ToolResponse<ProjectTransactionResult>.Fail("Transaction id is invalid.", "TRANSACTION_ID_INVALID", exception.Message);
        }

        if (!File.Exists(manifest))
        {
            return ToolResponse<ProjectTransactionResult>.Fail($"Transaction was not found: {transactionId}", "TRANSACTION_NOT_FOUND");
        }

        try
        {
            var record = JsonSerializer.Deserialize<ProjectTransactionRecord>(File.ReadAllText(manifest), JsonOptions);
            return record is null
                ? ToolResponse<ProjectTransactionResult>.Fail($"Transaction manifest is empty: {transactionId}", "TRANSACTION_INVALID")
                : ToolResponse<ProjectTransactionResult>.Ok($"Read transaction {transactionId}.", new ProjectTransactionResult(record, manifest));
        }
        catch (JsonException exception)
        {
            return ToolResponse<ProjectTransactionResult>.Fail($"Transaction manifest is invalid: {transactionId}", "TRANSACTION_INVALID", exception.Message);
        }
    }

    public ToolResponse<ProjectTransactionListResult> List(string projectPath)
    {
        var project = _projectDiscovery.GetSummary(projectPath);
        if (!project.Success || project.Data is null)
        {
            return ToolResponse<ProjectTransactionListResult>.Fail(project.Summary, project.Error?.Code ?? "PROJECT_NOT_FOUND", project.Error?.Message);
        }

        var root = Path.Combine(project.Data.ProjectRoot, ".pcbhelper", "transactions");
        if (!Directory.Exists(root))
        {
            return ToolResponse<ProjectTransactionListResult>.Ok("No transactions found.", new ProjectTransactionListResult(Array.Empty<ProjectTransactionRecord>()));
        }

        var records = Directory.GetDirectories(root)
            .Select(directory => Read(project.Data.ProjectRoot, Path.GetFileName(directory)).Data?.Transaction)
            .Where(static record => record is not null)
            .Cast<ProjectTransactionRecord>()
            .OrderByDescending(static record => record.CreatedAtUtc)
            .ToArray();
        return ToolResponse<ProjectTransactionListResult>.Ok($"Found {records.Length} transaction(s).", new ProjectTransactionListResult(records));
    }

    private static string ValidateId(string transactionId)
    {
        if (string.IsNullOrWhiteSpace(transactionId)
            || transactionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || transactionId.Contains(Path.DirectorySeparatorChar)
            || transactionId.Contains(Path.AltDirectorySeparatorChar)
            || transactionId is "." or "..")
        {
            throw new ArgumentException("Transaction id must be a single file-name-safe value.", nameof(transactionId));
        }

        return transactionId;
    }

    private static async Task WriteSnapshotAsync(string path, string? content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (content is null)
        {
            await File.WriteAllTextAsync(path + ".missing", string.Empty, cancellationToken);
        }
        else
        {
            await File.WriteAllTextAsync(path, content, cancellationToken);
        }
    }

    private static async Task<string?> ReadSnapshotAsync(string path, CancellationToken cancellationToken)
    {
        return File.Exists(path + ".missing") ? null : await File.ReadAllTextAsync(path, cancellationToken);
    }
}

public interface IProjectFileWriter
{
    Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken = default);
}

public sealed class AtomicProjectFileWriter : IProjectFileWriter
{
    public async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, content, cancellationToken);
            if (File.Exists(path))
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}

public sealed record PreparedFileChange(
    string RelativePath,
    string? BeforeContent,
    string? AfterContent,
    string BeforeHash,
    string AfterHash)
{
    public static PreparedFileChange Create(string relativePath, string? beforeContent, string? afterContent)
    {
        return new PreparedFileChange(
            relativePath,
            beforeContent,
            afterContent,
            ProjectTransactionService.ContentHash(beforeContent),
            ProjectTransactionService.ContentHash(afterContent));
    }
}

public sealed record PreparedOperation(string Id, string Type, string Summary);

public enum ProjectTransactionStatus
{
    Prepared,
    Applying,
    Applied,
    GatePassed,
    GateFailed,
    RolledBack,
    Incomplete
}

public sealed record ProjectTransactionRecord(
    string TransactionId,
    string Goal,
    string PlanHash,
    ProjectTransactionStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<TransactionOperationRecord> Operations,
    IReadOnlyList<TransactionFileRecord> Files,
    IReadOnlyList<string> AcknowledgedDecisionIds,
    EngineeringGateResult? Gate,
    string? Error);

public sealed record TransactionOperationRecord(string Id, string Type, string Summary);

public sealed record TransactionFileRecord(string RelativePath, string BeforeHash, string AfterHash);

public sealed record ProjectTransactionResult(ProjectTransactionRecord Transaction, string ManifestPath);

public sealed record ProjectTransactionListResult(IReadOnlyList<ProjectTransactionRecord> Transactions);
